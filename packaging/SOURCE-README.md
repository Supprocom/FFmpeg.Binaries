# Supprocom.FFmpeg.Source

This package contains the complete, exact upstream FFmpeg 9.0.2 source archive corresponding to the same-version Supprocom binary family, source-verification material, the complete release build definitions, the SHA-locked win-x86 dependency archive manifest, and all nine worker/toolchain provenance records. No patch is applied to FFmpeg source.

The complete FFmpeg source archive is not covered by one license. Individual upstream files use LGPL 2.1-or-later, GPL 2-or-later, GPL 3-or-later, and several permissive terms. `licenses/FFmpeg-LICENSE.md` describes the upstream layout; the four `COPYING` files contain the GPL and LGPL texts; file-specific permissive notices remain in the exact archive.

Version `9.0.2.1` corresponds to the full redistributable GPL-3.0-or-later binary family. Version `9.0.2-lgpl.1` corresponds to the binary family built with `--disable-gpl` and `--disable-nonfree`. Those binary-package license expressions describe the linked configurations, not the mixed-license complete FFmpeg source archive.

External shared-library identities, versions, repository evidence, and license material copied into each runtime payload are recorded in its worker provenance and notices. The build definitions identify the exact package-manager inputs used to obtain their corresponding sources.
