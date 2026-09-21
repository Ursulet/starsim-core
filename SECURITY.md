# Security Policy

## Official releases

Only installers and archives linked from [starsim.ro](https://starsim.ro/) or from the Releases page of the official StarSim Core GitHub repository are official StarSim Core distributions.

Forks and modified builds are permitted by the Apache License 2.0, but they are produced and distributed by their respective maintainers. Gîrdeanu Ștefan Victor and Asociația Star Sim do not review, endorse, warrant, support, or accept responsibility for third-party modifications or binaries, including builds that contain malicious code.

Before running a release, verify that its filename and SHA-256 checksum match the values published with that release. Do not install binaries obtained from unrelated download sites, shortened links, private messages, or unknown repositories.

## Native plugins

StarSim Core native plugins execute inside the application process and are not sandboxed. A malicious plugin can access the same files and resources as the current user. Install plugin DLLs only from developers and repositories you trust. The Plugin Manager cannot make an untrusted native DLL safe.

## Reporting a vulnerability

Please do not publish exploitable details before a fix is available.

Use GitHub Private Vulnerability Reporting on the official repository when enabled. If that channel is unavailable, contact Asociația Star Sim through [starsim.ro](https://starsim.ro/) and include:

- the affected StarSim Core version;
- Windows version and architecture;
- clear reproduction steps;
- relevant logs or screenshots with personal paths removed;
- the security impact you observed.

Security reports concern the official unmodified source and official release binaries. Problems introduced exclusively by a fork, repackaged installer, unofficial plugin, or modified binary must also be reported to that distributor.

## Supported version

Security fixes are provided for the latest public StarSim Core release. Older prerelease builds may be replaced rather than patched individually.

