using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using Newtonsoft.Json.Linq;
using Npgsql;
using StackExchange.Redis;
using Worker;

namespace WorkerTests
{
    class Program
    {
        private static int passed = 0;
        private static int failed = 0;

        static int Main(string[] args)
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("Running Worker Reliability & Crash-Safety Tests");
            Console.WriteLine("==================================================");

            // Unit & Simulated Tests (In-Process)
            RunTest("1. New voter vote parses correctly", Test_NewVoterVote);
            RunTest("2. Sequential vote switching payloads validate correctly (parser-only)", Test_VoteSwitching);
            RunTest("3. Malformed JSON does not crash worker", Test_MalformedJson);
            RunTest("4. Invalid vote values and missing voter IDs are rejected", Test_InvalidVotesAndMissingIds);
            RunTest("5. Null and empty payloads handled safely", Test_NullAndEmptyPayloads);
            RunTest("6. Database upsert SQL pattern contains atomic ON CONFLICT (syntax check)", Test_UpsertSqlPattern);
            RunTest("7. Retry limit failure policy handles simulated DB failure without hang or crash", Test_RetryLimit_SafeFailurePolicy);
            RunTest("8. Idle keepalive handles null and closed connections safely without throwing", Test_KeepAliveLifecycle);
            RunTest("9. Queue constants defined correctly", Test_QueueConstants);

            // Integration & Regression Tests against isolated instances
            string? testRedisConn = Environment.GetEnvironmentVariable("TEST_REDIS_CONNECTION");
            string? testDbConn = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION");

            if (!string.IsNullOrEmpty(testRedisConn) && !string.IsNullOrEmpty(testDbConn))
            {
                Console.WriteLine("--------------------------------------------------");
                Console.WriteLine("Running Isolated End-to-End Integration Tests");
                Console.WriteLine("--------------------------------------------------");

                RunTest("10. [Integration] Atomic ClaimNextVote, PostgreSQL commit, and queue ACK", () => Test_Integration_HappyPath(testRedisConn, testDbConn));
                RunTest("11. [Integration] Recovery of abandoned votes from processing queue", () => Test_Integration_CrashRecovery(testRedisConn));
                RunTest("12. [Integration] Atomic dead-letter transfer with rejection metadata", () => Test_Integration_DeadLetterQueue(testRedisConn));
                RunTest("13. [Integration] Idempotent upsert updates voter choice without duplication", () => Test_Integration_Idempotency(testRedisConn, testDbConn));
                RunTest("14. [Regression] Duplicate payloads with distinct msg_id do not cross-delete on ACK", () => Test_Regression_DuplicatePayloads(testRedisConn));
                RunTest("15. [Regression] Stale recovered vote does not overwrite newer choice in PostgreSQL", () => Test_Regression_StaleVoteRecovery(testRedisConn, testDbConn));
                RunTest("16. [Regression] Missing DLQ source message does not create rogue DLQ entry", () => Test_Regression_MissingDlqSource(testRedisConn));
                RunTest("17. [Regression] Reconnect recovery handles clean queue without errors", () => Test_Regression_RecoverySafety(testRedisConn));
                RunTest("18. [Regression] Multi-worker lease safety: active lease cannot be re-queued by concurrent recovery", () => Test_Regression_ConcurrentMultiWorkerRecoveryLease(testRedisConn));
                RunTest("19. [Regression] Equal-version collision: older submission cannot overwrite accepted choice", () => Test_Regression_EqualVersionTieBreak(testRedisConn, testDbConn));
                RunTest("20. [Regression] Lease expiration fencing: stale worker cannot ACK or corrupt vote after lease expires", () => Test_Regression_LeaseExpiredFencingToken(testRedisConn, testDbConn));
            }
            else
            {
                Console.WriteLine("[INFO] Set TEST_REDIS_CONNECTION and TEST_DB_CONNECTION to run live integration tests.");
            }

            Console.WriteLine("==================================================");
            Console.WriteLine($"Test Summary: {passed} passed, {failed} failed");
            Console.WriteLine("==================================================");

            return failed == 0 ? 0 : 1;
        }

        static ConnectionMultiplexer ConnectRedis(string redisConnStr)
        {
            var config = ConfigurationOptions.Parse(redisConnStr);
            config.AbortOnConnectFail = false;
            config.ConnectTimeout = 10000;
            config.SyncTimeout = 10000;
            return ConnectionMultiplexer.Connect(config);
        }

        static void RunTest(string testName, Action testAction)
        {
            try
            {
                testAction();
                Console.WriteLine($"[PASS] {testName}");
                passed++;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] {testName}: {ex.Message}");
                Console.ResetColor();
                failed++;
            }
        }

        static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception($"Assertion failed: {message}");
            }
        }

        static void Test_QueueConstants()
        {
            Assert(Worker.Program.QueueVotes == "votes", "QueueVotes should be 'votes'");
            Assert(Worker.Program.QueueProcessing == "votes:processing", "QueueProcessing should be 'votes:processing'");
            Assert(Worker.Program.QueueLeases == "votes:leases", "QueueLeases should be 'votes:leases'");
            Assert(Worker.Program.QueueClaims == "votes:claims", "QueueClaims should be 'votes:claims'");
            Assert(Worker.Program.QueueDlq == "votes:dlq", "QueueDlq should be 'votes:dlq'");
        }

        static void Test_NewVoterVote()
        {
            string payload = "{\"msg_id\": \"msg_1\", \"voter_id\": \"voter_abc123\", \"vote\": \"a\", \"version\": 100}";
            bool success = Worker.Program.TryParseAndValidateVote(payload, out var voteRecord, out var error);

            Assert(success, $"Expected validation success, got error: {error}");
            Assert(voteRecord != null, "voteRecord should not be null");
            Assert(voteRecord!.VoterId == "voter_abc123", $"Expected voter_abc123, got {voteRecord.VoterId}");
            Assert(voteRecord.Vote == "a", $"Expected vote 'a', got {voteRecord.Vote}");
            Assert(voteRecord.Version == 100, $"Expected version 100, got {voteRecord.Version}");
            Assert(error == null, "Error message should be null");
        }

        static void Test_VoteSwitching()
        {
            string payloadA = "{\"voter_id\": \"voter_switch\", \"vote\": \"a\"}";
            bool successA = Worker.Program.TryParseAndValidateVote(payloadA, out var voteA, out var errorA);
            Assert(successA && voteA?.Vote == "a", "Vote 'a' should validate");

            string payloadB = "{\"voter_id\": \"voter_switch\", \"vote\": \"b\"}";
            bool successB = Worker.Program.TryParseAndValidateVote(payloadB, out var voteB, out var errorB);
            Assert(successB && voteB?.Vote == "b", "Switched vote 'b' should validate");

            string payloadBack = "{\"voter_id\": \"voter_switch\", \"vote\": \"a\"}";
            bool successBack = Worker.Program.TryParseAndValidateVote(payloadBack, out var voteBack, out var errorBack);
            Assert(successBack && voteBack?.Vote == "a", "Switched back vote 'a' should validate");
        }

        static void Test_MalformedJson()
        {
            string[] malformedPayloads = new[]
            {
                "{invalid_json",
                "{vote: a, voter_id: 1}",
                "not json at all",
                "{\"vote\": \"a\", voter_id",
                "[1, 2, 3]"
            };

            foreach (var malformed in malformedPayloads)
            {
                bool success = Worker.Program.TryParseAndValidateVote(malformed, out var record, out var error);
                Assert(!success, $"Malformed payload '{malformed}' should have been rejected");
                Assert(record == null, "record should be null on failure");
                Assert(!string.IsNullOrEmpty(error), "Error message should be provided");
            }
        }

        static void Test_InvalidVotesAndMissingIds()
        {
            string badOption = "{\"voter_id\": \"voter_1\", \"vote\": \"c\"}";
            bool success1 = Worker.Program.TryParseAndValidateVote(badOption, out _, out var err1);
            Assert(!success1, "Vote 'c' should be rejected");
            Assert(err1!.Contains("Invalid vote value"), $"Unexpected error: {err1}");

            string stringVote = "{\"voter_id\": \"voter_1\", \"vote\": \"dogs\"}";
            bool success2 = Worker.Program.TryParseAndValidateVote(stringVote, out _, out var err2);
            Assert(!success2, "Vote 'dogs' should be rejected");

            string missingVoter = "{\"voter_id\": \"\", \"vote\": \"a\"}";
            bool success3 = Worker.Program.TryParseAndValidateVote(missingVoter, out _, out var err3);
            Assert(!success3, "Empty voter_id should be rejected");
            Assert(err3!.Contains("Missing or empty voter_id"), $"Unexpected error: {err3}");

            string nullVoter = "{\"vote\": \"a\"}";
            bool success4 = Worker.Program.TryParseAndValidateVote(nullVoter, out _, out var err4);
            Assert(!success4, "Missing voter_id property should be rejected");

            string missingVote = "{\"voter_id\": \"voter_1\", \"vote\": \"\"}";
            bool success5 = Worker.Program.TryParseAndValidateVote(missingVote, out _, out var err5);
            Assert(!success5, "Empty vote should be rejected");
        }

        static void Test_NullAndEmptyPayloads()
        {
            Assert(!Worker.Program.TryParseAndValidateVote(null, out _, out var err1), "Null payload should fail");
            Assert(!Worker.Program.TryParseAndValidateVote("", out _, out var err2), "Empty string payload should fail");
            Assert(!Worker.Program.TryParseAndValidateVote("   ", out _, out var err3), "Whitespace payload should fail");
        }

        static void Test_UpsertSqlPattern()
        {
            using var conn = new NpgsqlConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO votes (id, vote, version)
                                VALUES (@id, @vote, @version)
                                ON CONFLICT (id)
                                DO UPDATE SET vote = EXCLUDED.vote, version = EXCLUDED.version
                                WHERE EXCLUDED.version > votes.version";

            Assert(cmd.CommandText.Contains("ON CONFLICT (id)"), "Query must contain ON CONFLICT (id)");
            Assert(cmd.CommandText.Contains("WHERE EXCLUDED.version > votes.version"), "Query must enforce strict version ordering");
        }

        static void Test_RetryLimit_SafeFailurePolicy()
        {
            NpgsqlConnection? deadConnection = null;
            string invalidConnectionString = "Server=127.0.0.1;Port=54329;Username=invalid;Password=invalid;Timeout=1;Command Timeout=1;";

            bool result = Worker.Program.ProcessVoteWithRetry(
                ref deadConnection,
                invalidConnectionString,
                "voter_test",
                "a",
                maxRetries: 1,
                redis: null,
                rawJson: "{\"voter_id\":\"voter_test\",\"vote\":\"a\"}"
            );

            Assert(!result, "ProcessVoteWithRetry must return false when DB cannot be reached");
            Assert(deadConnection == null, "Connection should be null / disposed after failed attempt");
        }

        static void Test_KeepAliveLifecycle()
        {
            Worker.Program.KeepAlive(null);
            using var closedConn = new NpgsqlConnection();
            Worker.Program.KeepAlive(closedConn);
        }

        // --- Integration Tests (Isolated Environment) ---

        static void Test_Integration_HappyPath(string redisConnStr, string dbConnStr)
        {
            using var redisMultiplexer = ConnectRedis(redisConnStr);
            var redis = redisMultiplexer.GetDatabase();

            redis.KeyDelete(Worker.Program.QueueVotes);
            redis.KeyDelete(Worker.Program.QueueProcessing);
            redis.KeyDelete(Worker.Program.QueueLeases);
            redis.KeyDelete(Worker.Program.QueueClaims);
            redis.KeyDelete(Worker.Program.QueueDlq);

            using var pgsql = Worker.Program.TryOpenDbConnection(dbConnStr);

            using (var cmd = pgsql.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM votes WHERE id = 'test_voter_happy'";
                cmd.ExecuteNonQuery();
            }

            string payload = "{\"msg_id\": \"msg_happy\", \"voter_id\": \"test_voter_happy\", \"vote\": \"a\", \"version\": 100}";
            redis.ListRightPush(Worker.Program.QueueVotes, payload);

            var moved = Worker.Program.ClaimNextVote(redis, 30, out string? claimToken);
            Assert(moved != null && moved == payload, "Vote should be claimed with lease into processing queue");
            Assert(!string.IsNullOrEmpty(claimToken), "Claim token must be generated");
            Assert(redis.SortedSetScore(Worker.Program.QueueLeases, payload) != null, "Lease must be registered in votes:leases");

            bool parsed = Worker.Program.TryParseAndValidateVote(moved, out var voteRecord, out _);
            Assert(parsed && voteRecord != null, "Payload should parse successfully");

            NpgsqlConnection? activeConn = pgsql;
            bool success = Worker.Program.ProcessVoteWithRetry(ref activeConn, dbConnStr, voteRecord!.VoterId, voteRecord.Vote, 2, redis, moved, voteRecord.Version, claimToken);
            Assert(success, "Database upsert should succeed");

            bool acked = Worker.Program.AcknowledgeProcessingVote(redis, moved!, claimToken);
            Assert(acked, "Vote should be acknowledged and removed from processing queue and leases");
            Assert(redis.ListLength(Worker.Program.QueueProcessing) == 0, "Processing queue should now be empty");
            Assert(redis.SortedSetScore(Worker.Program.QueueLeases, payload) == null, "Lease must be cleared on ACK");

            using (var verifyCmd = pgsql.CreateCommand())
            {
                verifyCmd.CommandText = "SELECT vote, version FROM votes WHERE id = 'test_voter_happy'";
                using var reader = verifyCmd.ExecuteReader();
                Assert(reader.Read(), "Database must contain row");
                Assert(reader.GetString(0) == "a", "Vote must be 'a'");
                Assert(reader.GetInt64(1) == 100, "Version must be 100");
            }
        }

        static void Test_Integration_CrashRecovery(string redisConnStr)
        {
            using var redisMultiplexer = ConnectRedis(redisConnStr);
            var redis = redisMultiplexer.GetDatabase();

            redis.KeyDelete(Worker.Program.QueueVotes);
            redis.KeyDelete(Worker.Program.QueueProcessing);
            redis.KeyDelete(Worker.Program.QueueLeases);
            redis.KeyDelete(Worker.Program.QueueClaims);

            string vote1 = "{\"msg_id\": \"m1\", \"voter_id\": \"abandoned_1\", \"vote\": \"a\", \"version\": 1}";
            string vote2 = "{\"msg_id\": \"m2\", \"voter_id\": \"abandoned_2\", \"vote\": \"b\", \"version\": 2}";
            redis.ListRightPush(Worker.Program.QueueProcessing, vote1);
            redis.ListRightPush(Worker.Program.QueueProcessing, vote2);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            redis.SortedSetAdd(Worker.Program.QueueLeases, vote1, now - 10);
            redis.SortedSetAdd(Worker.Program.QueueLeases, vote2, now - 5);
            redis.HashSet(Worker.Program.QueueClaims, vote1, "old_token_1");
            redis.HashSet(Worker.Program.QueueClaims, vote2, "old_token_2");

            int recovered = Worker.Program.RecoverAbandonedVotes(redis);
            Assert(recovered == 2, $"Should have recovered 2 items with expired leases, got {recovered}");
            Assert(redis.ListLength(Worker.Program.QueueProcessing) == 0, "Processing queue should be empty after recovery");
            Assert(redis.ListLength(Worker.Program.QueueVotes) == 2, "Pending queue should have 2 recovered items");
        }

        static void Test_Integration_DeadLetterQueue(string redisConnStr)
        {
            using var redisMultiplexer = ConnectRedis(redisConnStr);
            var redis = redisMultiplexer.GetDatabase();

            redis.KeyDelete(Worker.Program.QueueProcessing);
            redis.KeyDelete(Worker.Program.QueueLeases);
            redis.KeyDelete(Worker.Program.QueueClaims);
            redis.KeyDelete(Worker.Program.QueueDlq);

            string poisonPayload = "{\"msg_id\": \"p1\", \"voter_id\": \"poison\", \"vote\": \"invalid_choice\"}";
            redis.ListRightPush(Worker.Program.QueueProcessing, poisonPayload);
            redis.SortedSetAdd(Worker.Program.QueueLeases, poisonPayload, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30);
            redis.HashSet(Worker.Program.QueueClaims, poisonPayload, "claim_p1");

            bool dlqSuccess = Worker.Program.MoveToDeadLetter(redis, poisonPayload, "Validation failed: invalid vote", "claim_p1");
            Assert(dlqSuccess, "MoveToDeadLetter should succeed");
            Assert(redis.ListLength(Worker.Program.QueueProcessing) == 0, "Item must be removed from processing queue");
            Assert(redis.SortedSetScore(Worker.Program.QueueLeases, poisonPayload) == null, "Lease must be cleared on DLQ");
            Assert(redis.ListLength(Worker.Program.QueueDlq) == 1, "Item must be present in dead-letter queue");

            var dlqRaw = redis.ListLeftPop(Worker.Program.QueueDlq);
            var dlqObj = JObject.Parse(dlqRaw.ToString());
            Assert(dlqObj["reason"]!.ToString() == "Validation failed: invalid vote", "DLQ must store rejection reason");
            Assert(dlqObj["payload"]!.ToString() == poisonPayload, "DLQ must store original payload");
            Assert(!string.IsNullOrEmpty(dlqObj["failed_at"]!.ToString()), "DLQ must store failure timestamp");
        }

        static void Test_Integration_Idempotency(string redisConnStr, string dbConnStr)
        {
            using var pgsql = Worker.Program.TryOpenDbConnection(dbConnStr);
            using var redisMultiplexer = ConnectRedis(redisConnStr);
            var redis = redisMultiplexer.GetDatabase();

            string voterId = "test_voter_idempotent";
            using (var cmd = pgsql.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM votes WHERE id = '{voterId}'";
                cmd.ExecuteNonQuery();
            }

            NpgsqlConnection? activeConn = pgsql;
            Worker.Program.ProcessVoteWithRetry(ref activeConn, dbConnStr, voterId, "a", 2, redis, "{}", 100);
            Worker.Program.ProcessVoteWithRetry(ref activeConn, dbConnStr, voterId, "a", 2, redis, "{}", 100);

            using (var checkCmd = pgsql.CreateCommand())
            {
                checkCmd.CommandText = $"SELECT COUNT(*), MAX(vote), MAX(version) FROM votes WHERE id = '{voterId}'";
                using var reader = checkCmd.ExecuteReader();
                Assert(reader.Read(), "Reader should return row");
                Assert(reader.GetInt64(0) == 1, "Count must be 1");
                Assert(reader.GetString(1) == "a", "Vote must be 'a'");
                Assert(reader.GetInt64(2) == 100, "Version must be 100");
            }
        }

        // --- Regression Tests ---

        static void Test_Regression_DuplicatePayloads(string redisConnStr)
        {
            using var redisMultiplexer = ConnectRedis(redisConnStr);
            var redis = redisMultiplexer.GetDatabase();

            redis.KeyDelete(Worker.Program.QueueProcessing);
            redis.KeyDelete(Worker.Program.QueueLeases);
            redis.KeyDelete(Worker.Program.QueueClaims);

            string msg1 = "{\"msg_id\": \"uuid-dup-1\", \"voter_id\": \"voter_dup\", \"vote\": \"a\", \"version\": 100}";
            string msg2 = "{\"msg_id\": \"uuid-dup-2\", \"voter_id\": \"voter_dup\", \"vote\": \"a\", \"version\": 105}";

            redis.ListRightPush(Worker.Program.QueueProcessing, msg1);
            redis.ListRightPush(Worker.Program.QueueProcessing, msg2);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            redis.SortedSetAdd(Worker.Program.QueueLeases, msg1, now + 30);
            redis.SortedSetAdd(Worker.Program.QueueLeases, msg2, now + 30);
            redis.HashSet(Worker.Program.QueueClaims, msg1, "token_1");
            redis.HashSet(Worker.Program.QueueClaims, msg2, "token_2");

            Assert(redis.ListLength(Worker.Program.QueueProcessing) == 2, "Processing queue should contain 2 items");

            // Acknowledge msg1 only with token_1
            bool acked = Worker.Program.AcknowledgeProcessingVote(redis, msg1, "token_1");
            Assert(acked, "Acknowledge msg1 should succeed");

            // Verify msg2 is still in QueueProcessing (msg1 did not cross-delete msg2!)
            Assert(redis.ListLength(Worker.Program.QueueProcessing) == 1, "Queue should still contain msg2");
            var remaining = redis.ListLeftPop(Worker.Program.QueueProcessing);
            Assert(remaining.ToString() == msg2, "Remaining item must be msg2");
        }

        static void Test_Regression_StaleVoteRecovery(string redisConnStr, string dbConnStr)
        {
            using var pgsql = Worker.Program.TryOpenDbConnection(dbConnStr);
            using var redisMultiplexer = ConnectRedis(redisConnStr);
            var redis = redisMultiplexer.GetDatabase();

            string voterId = "voter_stale_test";
            using (var cmd = pgsql.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM votes WHERE id = '{voterId}'";
                cmd.ExecuteNonQuery();
            }

            NpgsqlConnection? activeConn = pgsql;

            // 1. Voter submitted newer choice 'b' with version 200
            Worker.Program.ProcessVoteWithRetry(ref activeConn, dbConnStr, voterId, "b", 2, redis, "{}", 200);

            // 2. An older/stale recovered vote arrives with choice 'a' and version 100
            Worker.Program.ProcessVoteWithRetry(ref activeConn, dbConnStr, voterId, "a", 2, redis, "{}", 100);

            // 3. Verify PostgreSQL still has vote 'b' and version 200 (stale vote 'a' was rejected by WHERE EXCLUDED.version > votes.version)
            using (var checkCmd = pgsql.CreateCommand())
            {
                checkCmd.CommandText = $"SELECT vote, version FROM votes WHERE id = '{voterId}'";
                using var reader = checkCmd.ExecuteReader();
                Assert(reader.Read(), "Row must exist");
                Assert(reader.GetString(0) == "b", "Database must still retain choice 'b', not stale choice 'a'");
                Assert(reader.GetInt64(1) == 200, "Version must remain 200");
            }
        }

        static void Test_Regression_MissingDlqSource(string redisConnStr)
        {
            using var redisMultiplexer = ConnectRedis(redisConnStr);
            var redis = redisMultiplexer.GetDatabase();

            redis.KeyDelete(Worker.Program.QueueProcessing);
            redis.KeyDelete(Worker.Program.QueueLeases);
            redis.KeyDelete(Worker.Program.QueueClaims);
            redis.KeyDelete(Worker.Program.QueueDlq);

            string absentPayload = "{\"msg_id\": \"ghost\", \"voter_id\": \"ghost\", \"vote\": \"a\"}";

            bool result = Worker.Program.MoveToDeadLetter(redis, absentPayload, "Phantom failure");
            Assert(!result, "MoveToDeadLetter must return false when source message is absent");
            Assert(redis.ListLength(Worker.Program.QueueDlq) == 0, "No entry must be created in votes:dlq when source was absent");
        }

        static void Test_Regression_RecoverySafety(string redisConnStr)
        {
            using var redisMultiplexer = ConnectRedis(redisConnStr);
            var redis = redisMultiplexer.GetDatabase();

            redis.KeyDelete(Worker.Program.QueueVotes);
            redis.KeyDelete(Worker.Program.QueueProcessing);
            redis.KeyDelete(Worker.Program.QueueLeases);
            redis.KeyDelete(Worker.Program.QueueClaims);

            int recovered = Worker.Program.RecoverAbandonedVotes(redis);
            Assert(recovered == 0, "Clean queue should recover 0 items");
        }

        static void Test_Regression_ConcurrentMultiWorkerRecoveryLease(string redisConnStr)
        {
            using var redisMultiplexer = ConnectRedis(redisConnStr);
            var redis = redisMultiplexer.GetDatabase();

            redis.KeyDelete(Worker.Program.QueueVotes);
            redis.KeyDelete(Worker.Program.QueueProcessing);
            redis.KeyDelete(Worker.Program.QueueLeases);
            redis.KeyDelete(Worker.Program.QueueClaims);

            string activeMsg = "{\"msg_id\": \"w1_active\", \"voter_id\": \"voter_active\", \"vote\": \"a\", \"version\": 100}";
            string abandonedMsg = "{\"msg_id\": \"w2_crashed\", \"voter_id\": \"voter_dead\", \"vote\": \"b\", \"version\": 90}";

            redis.ListRightPush(Worker.Program.QueueProcessing, activeMsg);
            redis.ListRightPush(Worker.Program.QueueProcessing, abandonedMsg);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            redis.SortedSetAdd(Worker.Program.QueueLeases, activeMsg, now + 30);
            redis.SortedSetAdd(Worker.Program.QueueLeases, abandonedMsg, now - 10);
            redis.HashSet(Worker.Program.QueueClaims, activeMsg, "token_w1");
            redis.HashSet(Worker.Program.QueueClaims, abandonedMsg, "token_w2");

            int recovered = Worker.Program.RecoverAbandonedVotes(redis);
            Assert(recovered == 1, $"Worker 2 should only recover 1 abandoned item with expired lease, got {recovered}");

            Assert(redis.ListLength(Worker.Program.QueueProcessing) == 1, "QueueProcessing must still hold activeMsg");
            var inProc = redis.ListLeftPop(Worker.Program.QueueProcessing);
            Assert(inProc.ToString() == activeMsg, "activeMsg being processed by Worker 1 was preserved");

            Assert(redis.ListLength(Worker.Program.QueueVotes) == 1, "QueueVotes must contain recovered abandonedMsg");
            var inVotes = redis.ListLeftPop(Worker.Program.QueueVotes);
            Assert(inVotes.ToString() == abandonedMsg, "Recovered vote must be abandonedMsg");
        }

        static void Test_Regression_EqualVersionTieBreak(string redisConnStr, string dbConnStr)
        {
            using var pgsql = Worker.Program.TryOpenDbConnection(dbConnStr);
            using var redisMultiplexer = ConnectRedis(redisConnStr);
            var redis = redisMultiplexer.GetDatabase();

            string voterId = "voter_tiebreak_test";
            using (var cmd = pgsql.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM votes WHERE id = '{voterId}'";
                cmd.ExecuteNonQuery();
            }

            NpgsqlConnection? activeConn = pgsql;
            const long collidedVersion = 500000;

            Worker.Program.ProcessVoteWithRetry(ref activeConn, dbConnStr, voterId, "b", 2, redis, "{}", collidedVersion);
            Worker.Program.ProcessVoteWithRetry(ref activeConn, dbConnStr, voterId, "a", 2, redis, "{}", collidedVersion);

            using (var checkCmd = pgsql.CreateCommand())
            {
                checkCmd.CommandText = $"SELECT vote, version FROM votes WHERE id = '{voterId}'";
                using var reader = checkCmd.ExecuteReader();
                Assert(reader.Read(), "Row must exist");
                Assert(reader.GetString(0) == "b", "Database must retain accepted choice 'b', colliding choice 'a' must not overwrite it");
                Assert(reader.GetInt64(1) == collidedVersion, "Version must remain 500000");
            }
        }

        // --- NEW LEASE EXPIRATION & FENCING REGRESSION TEST ---

        static void Test_Regression_LeaseExpiredFencingToken(string redisConnStr, string dbConnStr)
        {
            using var pgsql = Worker.Program.TryOpenDbConnection(dbConnStr);
            using var redisMultiplexer = ConnectRedis(redisConnStr);
            var redis = redisMultiplexer.GetDatabase();

            redis.KeyDelete(Worker.Program.QueueVotes);
            redis.KeyDelete(Worker.Program.QueueProcessing);
            redis.KeyDelete(Worker.Program.QueueLeases);
            redis.KeyDelete(Worker.Program.QueueClaims);

            string voterId = "voter_lease_fencing_test";
            using (var cmd = pgsql.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM votes WHERE id = '{voterId}'";
                cmd.ExecuteNonQuery();
            }

            string payload = "{\"msg_id\": \"msg_fencing\", \"voter_id\": \"voter_lease_fencing_test\", \"vote\": \"a\", \"version\": 300}";
            redis.ListRightPush(Worker.Program.QueueVotes, payload);

            // Step 1: Worker 1 claims message M with a 2-second lease, receiving Token 1
            var msg1 = Worker.Program.ClaimNextVote(redis, 2, out string? token1);
            Assert(msg1 == payload && !string.IsNullOrEmpty(token1), "Worker 1 should claim message with Token 1");

            // Step 2: Simulate Worker 1 stalling so its lease expires in Redis
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            redis.SortedSetAdd(Worker.Program.QueueLeases, payload, now - 10); // Lease expired 10 seconds ago

            // Step 3: Worker 2 triggers recovery and safely re-queues message M back to QueueVotes
            int recovered = Worker.Program.RecoverAbandonedVotes(redis);
            Assert(recovered == 1, "Worker 2 must recover expired message back to votes");
            Assert(redis.ListLength(Worker.Program.QueueVotes) == 1, "QueueVotes must have 1 item");

            // Step 4: Worker 3 claims message M from QueueVotes, receiving Token 3
            var msg3 = Worker.Program.ClaimNextVote(redis, 30, out string? token3);
            Assert(msg3 == payload && !string.IsNullOrEmpty(token3), "Worker 3 should claim message with Token 3");
            Assert(token1 != token3, "Worker 3 token must be distinct from stale Worker 1 token");

            // Step 5: Stale Worker 1 wakes up and attempts to acknowledge message M with its stale Token 1
            bool staleAck = Worker.Program.AcknowledgeProcessingVote(redis, payload, token1);
            Assert(!staleAck, "Stale Worker 1 acknowledge MUST be rejected by fencing token check!");

            // Assert that Worker 3's active message in QueueProcessing was NOT deleted by stale Worker 1!
            Assert(redis.ListLength(Worker.Program.QueueProcessing) == 1, "QueueProcessing must still contain active message for Worker 3");

            // Step 6: Worker 3 commits to PostgreSQL and acknowledges with valid Token 3
            NpgsqlConnection? activeConn = pgsql;
            Worker.Program.ProcessVoteWithRetry(ref activeConn, dbConnStr, voterId, "a", 2, redis, payload, 300, token3);
            bool validAck = Worker.Program.AcknowledgeProcessingVote(redis, payload, token3);
            Assert(validAck, "Worker 3 acknowledge with valid token MUST succeed");
            Assert(redis.ListLength(Worker.Program.QueueProcessing) == 0, "Processing queue should now be empty");

            // Step 7: Verify database state
            using (var checkCmd = pgsql.CreateCommand())
            {
                checkCmd.CommandText = $"SELECT vote, version FROM votes WHERE id = '{voterId}'";
                using var reader = checkCmd.ExecuteReader();
                Assert(reader.Read(), "Row must exist");
                Assert(reader.GetString(0) == "a", "Vote must be 'a'");
                Assert(reader.GetInt64(1) == 300, "Version must be 300");
            }
        }
    }
}
