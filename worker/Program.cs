using System;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json;
using Npgsql;
using StackExchange.Redis;

namespace Worker
{
    public record VoteRecord(string MsgId, string VoterId, string Vote, long Version);

    public class Program
    {
        public const string QueueVotes = "votes";
        public const string QueueProcessing = "votes:processing";
        public const string QueueLeases = "votes:leases";
        public const string QueueClaims = "votes:claims";
        public const string QueueDlq = "votes:dlq";
        public const int DefaultLeaseTimeoutSeconds = 30;

        private const string DefaultDbConnectionString = "Server=db;Username=postgres;Password=postgres;";

        public static int Main(string[] args)
        {
            try
            {
                var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING") ?? DefaultDbConnectionString;
                var redisHost = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "redis";

                var pgsql = OpenDbConnection(connectionString);
                var redisConn = OpenRedisConnection(redisHost);
                var redis = redisConn.GetDatabase();

                // On startup, recover any abandoned votes left with expired leases
                RecoverAbandonedVotes(redis);

                while (true)
                {
                    // Slow down to prevent CPU spike, only query each 100ms
                    Thread.Sleep(100);

                    // Reconnect redis if down
                    if (redisConn == null || !redisConn.IsConnected)
                    {
                        Console.WriteLine("Reconnecting Redis");
                        try { redisConn?.Dispose(); } catch {}
                        redisConn = OpenRedisConnection(redisHost);
                        redis = redisConn.GetDatabase();

                        // Safe recovery across multiple workers: only re-queue expired leases
                        RecoverAbandonedVotes(redis);
                    }

                    string? json = null;
                    string? claimToken = null;
                    try
                    {
                        // Atomically claim next vote from pending queue into processing queue with a 30s lease and unique claim token
                        json = ClaimNextVote(redis, DefaultLeaseTimeoutSeconds, out claimToken);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error retrieving vote from Redis: {ex.InnerException?.Message ?? ex.Message}");
                        try { redisConn?.Dispose(); } catch {}
                        redisConn = null;
                        Thread.Sleep(1000);
                        continue;
                    }

                    if (json != null)
                    {
                        if (!TryParseAndValidateVote(json, out var voteRecord, out var validationError))
                        {
                            Console.Error.WriteLine($"Rejected invalid vote payload: {validationError}");
                            MoveToDeadLetter(redis, json, $"Validation error: {validationError}", claimToken);
                            continue;
                        }

                        Console.WriteLine($"Processing vote for '{voteRecord!.Vote}' by '{voteRecord.VoterId}' (v:{voteRecord.Version})");
                        bool success = ProcessVoteWithRetry(
                            ref pgsql,
                            connectionString,
                            voteRecord.VoterId,
                            voteRecord.Vote,
                            maxRetries: 3,
                            redis: redis,
                            rawJson: json,
                            version: voteRecord.Version,
                            claimToken: claimToken
                        );

                        if (success)
                        {
                            // Acknowledge by removing from processing queue and lease set using fencing token
                            bool acked = AcknowledgeProcessingVote(redis, json, claimToken);
                            if (!acked)
                            {
                                Console.Error.WriteLine($"Acknowledge rejected: lease expired or claimed by another worker for voter '{voteRecord.VoterId}'.");
                            }
                        }
                        else
                        {
                            Console.Error.WriteLine($"Database update failed for voter '{voteRecord.VoterId}'. Pausing before next queue pop.");
                            Thread.Sleep(2000);
                        }
                    }
                    else
                    {
                        KeepAlive(pgsql);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        public static string? ClaimNextVote(IDatabase redis, int leaseTimeoutSeconds, out string? claimToken)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long leaseExpiry = now + leaseTimeoutSeconds;
            claimToken = Guid.NewGuid().ToString();

            const string script = @"
                local msg = redis.call('LMOVE', KEYS[1], KEYS[2], 'LEFT', 'RIGHT')
                if msg then
                    redis.call('ZADD', KEYS[3], ARGV[1], msg)
                    redis.call('HSET', KEYS[4], msg, ARGV[2])
                    return msg
                else
                    return nil
                end";

            var result = redis.ScriptEvaluate(script,
                new RedisKey[] { QueueVotes, QueueProcessing, QueueLeases, QueueClaims },
                new RedisValue[] { leaseExpiry, claimToken });

            if (result.IsNull)
            {
                claimToken = null;
                return null;
            }

            return result.ToString();
        }

        public static string? ClaimNextVote(IDatabase redis, int leaseTimeoutSeconds = DefaultLeaseTimeoutSeconds)
        {
            return ClaimNextVote(redis, leaseTimeoutSeconds, out _);
        }

        public static bool RenewLease(IDatabase redis, string rawJson, string claimToken, int leaseTimeoutSeconds = DefaultLeaseTimeoutSeconds)
        {
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long newExpiry = now + leaseTimeoutSeconds;

                const string script = @"
                    local currentToken = redis.call('HGET', KEYS[2], ARGV[1])
                    if currentToken == ARGV[2] then
                        redis.call('ZADD', KEYS[1], ARGV[3], ARGV[1])
                        return 1
                    else
                        return 0
                    end";

                var result = (int)redis.ScriptEvaluate(script,
                    new RedisKey[] { QueueLeases, QueueClaims },
                    new RedisValue[] { rawJson, claimToken, newExpiry });

                return result == 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error renewing lease: {ex.Message}");
                return false;
            }
        }

        public static int RecoverAbandonedVotes(IDatabase redis, int leaseTimeoutSeconds = DefaultLeaseTimeoutSeconds)
        {
            int count = 0;
            try
            {
                const string recoveryLockKey = "votes:recovery:lock";
                string lockId = Guid.NewGuid().ToString();
                bool acquired = redis.LockTake(recoveryLockKey, lockId, TimeSpan.FromSeconds(10));
                if (!acquired)
                {
                    return 0;
                }

                try
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    // Safe multi-worker recovery:
                    // Only messages whose lease score is <= now are considered abandoned.
                    // Active messages being processed by other workers have score > now and are never re-queued.
                    const string script = @"
                        local processing_key = KEYS[1]
                        local leases_key = KEYS[2]
                        local votes_key = KEYS[3]
                        local claims_key = KEYS[4]
                        local now = tonumber(ARGV[1])
                        local count = 0

                        local expired = redis.call('ZRANGEBYSCORE', leases_key, 0, now)
                        for i = 1, #expired do
                            local msg = expired[i]
                            redis.call('ZREM', leases_key, msg)
                            redis.call('HDEL', claims_key, msg)
                            local removed = redis.call('LREM', processing_key, 1, msg)
                            if removed > 0 then
                                redis.call('LPUSH', votes_key, msg)
                                count = count + 1
                            end
                        end
                        return count";

                    var result = redis.ScriptEvaluate(script,
                        new RedisKey[] { QueueProcessing, QueueLeases, QueueVotes, QueueClaims },
                        new RedisValue[] { now });

                    count = (int)result;
                    if (count > 0)
                    {
                        Console.WriteLine($"Recovered {count} abandoned vote(s) from {QueueProcessing} to {QueueVotes}.");
                    }
                }
                finally
                {
                    redis.LockRelease(recoveryLockKey, lockId);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error recovering abandoned votes: {ex.Message}");
            }
            return count;
        }

        public static bool AcknowledgeProcessingVote(IDatabase redis, string rawJson, string? claimToken = null)
        {
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                const string script = @"
                    local expectedToken = ARGV[2]
                    local currentToken = redis.call('HGET', KEYS[3], ARGV[1])
                    local score = redis.call('ZSCORE', KEYS[2], ARGV[1])
                    local now = tonumber(ARGV[3])

                    if expectedToken ~= '' then
                        if currentToken ~= expectedToken then
                            return 0
                        end
                    end

                    if not score or tonumber(score) <= now then
                        return 0
                    end

                    redis.call('LREM', KEYS[1], 1, ARGV[1])
                    redis.call('ZREM', KEYS[2], ARGV[1])
                    redis.call('HDEL', KEYS[3], ARGV[1])
                    return 1";

                var result = (int)redis.ScriptEvaluate(script,
                    new RedisKey[] { QueueProcessing, QueueLeases, QueueClaims },
                    new RedisValue[] { rawJson, claimToken ?? "", now });

                return result == 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to acknowledge vote from {QueueProcessing}: {ex.Message}");
                return false;
            }
        }

        public static bool MoveToDeadLetter(IDatabase redis, string rawJson, string reason, string? claimToken = null)
        {
            try
            {
                var dlqEnvelope = JsonConvert.SerializeObject(new
                {
                    reason,
                    failed_at = DateTime.UtcNow.ToString("o"),
                    payload = rawJson
                });

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Atomic transfer to DLQ only if message is present in QueueProcessing and lease is held
                const string script = @"
                    local expectedToken = ARGV[3]
                    local currentToken = redis.call('HGET', KEYS[4], ARGV[1])
                    local score = redis.call('ZSCORE', KEYS[2], ARGV[1])
                    local now = tonumber(ARGV[4])

                    if expectedToken ~= '' then
                        if currentToken ~= expectedToken then
                            return 0
                        end
                    end

                    if not score or tonumber(score) <= now then
                        return 0
                    end

                    local removed = redis.call('LREM', KEYS[1], 1, ARGV[1])
                    redis.call('ZREM', KEYS[2], ARGV[1])
                    redis.call('HDEL', KEYS[4], ARGV[1])
                    if removed > 0 then
                        redis.call('RPUSH', KEYS[3], ARGV[2])
                        return 1
                    else
                        return 0
                    end";

                var result = (int)redis.ScriptEvaluate(script,
                    new RedisKey[] { QueueProcessing, QueueLeases, QueueDlq, QueueClaims },
                    new RedisValue[] { rawJson, dlqEnvelope, claimToken ?? "", now });

                if (result == 1)
                {
                    Console.Error.WriteLine($"Moved vote to dead-letter queue ({QueueDlq}): {reason}");
                    return true;
                }
                else
                {
                    Console.Error.WriteLine($"Message was absent or lease expired in {QueueProcessing}; skipped DLQ push to avoid duplicate.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to move vote to {QueueDlq}: {ex.Message}");
                return false;
            }
        }

        public static bool TryParseAndValidateVote(string? json, out VoteRecord? voteRecord, out string? errorMessage)
        {
            voteRecord = null;
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                errorMessage = "Payload is null or empty.";
                return false;
            }

            try
            {
                var definition = new { msg_id = "", vote = "", voter_id = "", version = 0L };
                var parsed = JsonConvert.DeserializeAnonymousType(json, definition);

                if (parsed == null)
                {
                    errorMessage = "Payload deserialized to null.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(parsed.voter_id))
                {
                    errorMessage = "Missing or empty voter_id.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(parsed.vote))
                {
                    errorMessage = "Missing or empty vote.";
                    return false;
                }

                if (parsed.vote != "a" && parsed.vote != "b")
                {
                    errorMessage = $"Invalid vote value '{parsed.vote}'. Allowed values are 'a' or 'b'.";
                    return false;
                }

                string msgId = string.IsNullOrWhiteSpace(parsed.msg_id) ? Guid.NewGuid().ToString() : parsed.msg_id;
                long version = parsed.version;

                voteRecord = new VoteRecord(msgId, parsed.voter_id, parsed.vote, version);
                return true;
            }
            catch (JsonException ex)
            {
                errorMessage = $"Malformed JSON: {ex.Message}";
                return false;
            }
        }

        public static bool ProcessVoteWithRetry(
            ref NpgsqlConnection? pgsql,
            string connectionString,
            string voterId,
            string vote,
            int maxRetries = 3,
            IDatabase? redis = null,
            string? rawJson = null,
            long version = 0,
            string? claimToken = null)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                // Heartbeat / lease renewal before each attempt if claim token provided
                if (redis != null && !string.IsNullOrEmpty(rawJson) && !string.IsNullOrEmpty(claimToken))
                {
                    bool renewed = RenewLease(redis, rawJson, claimToken);
                    if (!renewed && attempt > 1)
                    {
                        Console.Error.WriteLine($"Lease for voter '{voterId}' expired or claimed by another worker; fencing out stale processing.");
                        return false;
                    }
                }

                try
                {
                    if (pgsql == null || pgsql.State != System.Data.ConnectionState.Open)
                    {
                        Console.WriteLine("Reconnecting DB");
                        try { pgsql?.Dispose(); } catch {}
                        pgsql = null;
                        pgsql = TryOpenDbConnection(connectionString);
                    }

                    if (pgsql != null)
                    {
                        UpdateVote(pgsql, voterId, vote, version);
                        return true;
                    }
                }
                catch (Exception ex) when (ex is SocketException || ex is NpgsqlException || ex is DbException)
                {
                    Console.Error.WriteLine($"Database operation failed on attempt {attempt}/{maxRetries}: {ex.Message}");
                    try { pgsql?.Dispose(); } catch {}
                    pgsql = null;
                }

                if (attempt < maxRetries)
                {
                    Thread.Sleep(1000);
                }
            }

            // Retry limit reached: execute safe failure policy
            Console.Error.WriteLine($"Exhausted {maxRetries} database retries for vote by '{voterId}'.");

            if (redis != null && !string.IsNullOrEmpty(rawJson))
            {
                MoveToDeadLetter(redis, rawJson, $"Exhausted {maxRetries} database retries for voter '{voterId}'", claimToken);
            }

            return false;
        }

        public static bool UpdateVote(NpgsqlConnection connection, string voterId, string vote, long version = 0)
        {
            using var command = connection.CreateCommand();
            // Strict inequality: only updates if incoming version is strictly > existing version.
            // When versions collide (equal version), the accepted choice is preserved and older/colliding writes are rejected.
            command.CommandText = @"INSERT INTO votes (id, vote, version)
                                    VALUES (@id, @vote, @version)
                                    ON CONFLICT (id)
                                    DO UPDATE SET 
                                        vote = EXCLUDED.vote,
                                        version = EXCLUDED.version
                                    WHERE EXCLUDED.version > votes.version";

            var idParam = command.CreateParameter();
            idParam.ParameterName = "@id";
            idParam.Value = voterId;
            command.Parameters.Add(idParam);

            var voteParam = command.CreateParameter();
            voteParam.ParameterName = "@vote";
            voteParam.Value = vote;
            command.Parameters.Add(voteParam);

            var verParam = command.CreateParameter();
            verParam.ParameterName = "@version";
            verParam.Value = version;
            command.Parameters.Add(verParam);

            int rows = command.ExecuteNonQuery();
            return rows > 0;
        }

        public static void KeepAlive(NpgsqlConnection? connection)
        {
            if (connection != null && connection.State == System.Data.ConnectionState.Open)
            {
                try
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT 1";
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"DB keep-alive failed: {ex.Message}");
                    try { connection.Close(); } catch {}
                }
            }
        }

        public static NpgsqlConnection TryOpenDbConnection(string connectionString)
        {
            var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            EnsureTableExists(connection);
            return connection;
        }

        public static NpgsqlConnection OpenDbConnection(string connectionString)
        {
            while (true)
            {
                try
                {
                    var connection = TryOpenDbConnection(connectionString);
                    Console.Error.WriteLine("Connected to db");
                    return connection;
                }
                catch (Exception ex) when (ex is SocketException || ex is DbException)
                {
                    Console.Error.WriteLine("Waiting for db");
                    Thread.Sleep(1000);
                }
            }
        }

        public static void EnsureTableExists(NpgsqlConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"CREATE TABLE IF NOT EXISTS votes (
                                        id VARCHAR(255) NOT NULL UNIQUE,
                                        vote VARCHAR(255) NOT NULL,
                                        version BIGINT NOT NULL DEFAULT 0
                                    );
                                    ALTER TABLE votes ADD COLUMN IF NOT EXISTS version BIGINT NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
        }

        private static ConnectionMultiplexer OpenRedisConnection(string hostname)
        {
            while (true)
            {
                try
                {
                    var ipAddress = GetIp(hostname);
                    Console.WriteLine($"Found redis at {ipAddress}");
                    Console.Error.WriteLine("Connecting to redis");
                    return ConnectionMultiplexer.Connect(ipAddress);
                }
                catch (Exception ex) when (ex is RedisConnectionException || ex is SocketException)
                {
                    Console.Error.WriteLine("Waiting for redis");
                    Thread.Sleep(1000);
                }
            }
        }

        private static string GetIp(string hostname)
            => Dns.GetHostEntry(hostname)
                .AddressList
                .First(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToString();
    }
}
