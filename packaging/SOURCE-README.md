# Supprocom.FFmpeg.Source

This package contains the complete, exact upstream FFmpeg source archive corresponding to the same-version Supprocom binary package family, together with source-verification material, release build definitions, worker provenance, and a statement that no source patch was applied.

The complete source archive is not LGPL-only. Individual upstream files are covered by LGPL 2.1-or-later, GPL 2-or-later, GPL 3-or-later, and several permissive terms. `licenses/FFmpeg-LICENSE.md` describes the upstream licensing layout; `licenses/COPYING.GPLv2`, `licenses/COPYING.GPLv3`, `licenses/COPYING.LGPLv2.1`, and `licenses/COPYING.LGPLv3` contain the corresponding license texts. File-specific permissive notices remain in the exact source archive.

The separately published native binary packages are built with `--disable-gpl` and `--disable-nonfree`; their LGPL claim is scoped to that binary configuration and does not describe this complete source archive.
