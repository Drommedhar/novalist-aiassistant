"""Injected only into opt-in integration test workers via PYTHONPATH."""
import sys


def block_network(event, args):
    if event in ("socket.connect", "socket.getaddrinfo"):
        raise RuntimeError("Network disabled during the dictation integration test")


sys.addaudithook(block_network)
