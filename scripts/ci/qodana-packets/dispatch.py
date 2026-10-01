#!/usr/bin/env python3
"""Dispatch only the reviewed packet/stage supported by its verifier."""
import os
from pathlib import Path
import verify

selection = verify.read_selection(Path(__file__).resolve().parent, os.environ["PACKET_BRANCH"])
if selection["packet"] in {"P11", "P13", "P14"}:
    import r2_verify
    r2_verify.main()
else:
    verify.main()
