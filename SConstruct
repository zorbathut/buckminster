# Untyped shim, deliberately logic-free: SCons injects globals pyright can't model, so this file sits outside the pyright project entirely (the [tool.pyright] config in tools/lib/pyproject.toml covers tools/ only). All target definitions live in tools/lib/buildtargets.py, typed and pyright-enforced.
import os
import sys

# append, not insert(0): tools/ has modules named like packages others use (build.py vs PyPA build, test.py vs stdlib test), and stdlib/site-packages must stay in front.
sys.path.append(os.path.join(os.getcwd(), "tools"))

from lib import buildtargets

buildtargets.define()
