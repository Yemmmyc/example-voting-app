from flask import Flask, render_template, request, make_response, g
import os
import socket
import random
import json
import logging
import time
import uuid

option_a = os.getenv('OPTION_A', "Cats")
option_b = os.getenv('OPTION_B', "Dogs")
hostname = socket.gethostname()

app = Flask(__name__)

gunicorn_error_logger = logging.getLogger('gunicorn.error')
app.logger.handlers.extend(gunicorn_error_logger.handlers)
app.logger.setLevel(logging.INFO)

def get_redis():
    if not hasattr(g, 'redis'):
        import redis
        g.redis = redis.Redis(host="redis", db=0, socket_timeout=5)
    return g.redis

@app.route("/", methods=['POST','GET'])
def hello():
    voter_id = request.cookies.get('voter_id')
    if not voter_id:
        voter_id = hex(random.getrandbits(64))[2:-1]

    vote = None

    if request.method == 'POST':
        redis = get_redis()
        vote = request.form['vote']
        app.logger.info('Received vote for %s', vote)
        msg_id = str(uuid.uuid4())
        # Monotonic timestamp + per-voter sequence tie-breaker
        epoch_ms = int(time.time() * 1000)
        voter_seq = redis.incr(f"voter:{voter_id}:seq")
        version = (epoch_ms * 10000) + (voter_seq % 10000)
        data = json.dumps({
            'msg_id': msg_id,
            'voter_id': voter_id,
            'vote': vote,
            'version': version
        })
        redis.rpush('votes', data)

    resp = make_response(render_template(
        'index.html',
        option_a=option_a,
        option_b=option_b,
        hostname=hostname,
        vote=vote,
    ))
    resp.set_cookie('voter_id', voter_id)
    return resp


if __name__ == "__main__":
    app.run(host='0.0.0.0', port=80, debug=True, threaded=True)
