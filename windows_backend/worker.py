"""One isolated, read-only user-dump request over inherited pipes."""
import json
import sys

from .dump import inspect_dump


if __name__ == "__main__":
    try:
        receipt = {"result": inspect_dump(**json.load(sys.stdin))}
    except Exception as error:
        receipt = {"error": {"code": getattr(error, "code", "USER_DUMP_ERROR"), "message": str(error)}}
    print(json.dumps(receipt), flush=True)
