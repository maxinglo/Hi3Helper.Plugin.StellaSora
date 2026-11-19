# Third-party notices

## Hi3Helper.Plugin.Core

- Source: https://github.com/CollapseLauncher/Hi3Helper.Plugin.Core
- Revision: `8c5a18525331d550777d2831943a4858a7fc940c`
- API standard: `0.1.6`
- License: MIT, Copyright (c) 2025 Collapse Launcher.
- The source snapshot in `vendor/Hi3Helper.Plugin.Core` was copied from the reference NTE project's pinned submodule.
  Build output, Git metadata, docs, and its sample test application are excluded. Runtime source files are unchanged.
- License text: `vendor/Hi3Helper.Plugin.Core/LICENSE`, included as `Hi3Helper.Plugin.Core.LICENSE.txt` in plugin
  packages.

## Microsoft.Extensions.Logging.Abstractions

- Source: https://github.com/dotnet/runtime
- Version: `10.0.13`
- License: MIT, .NET Foundation and Contributors. The .NET license and runtime third-party notices are included in
  `vendor/licenses/` and in plugin packages, covering the linked runtime as well.

## Official game assets

The embedded game icon comes from the installed official Stella Sora CN launcher 1.3.0 (`resources/build.ico` inside
`app.asar`). Backgrounds, banners, news, and social links are provided by Yostar's official launcher API. Game names,
artwork, and trademarks belong to their respective owners; the source-code MIT license does not relicense those assets.
The plugin is a community project and is not affiliated with Yostar.

Social icons use SVG symbols from the official Windows launchers, rendered as transparent PNGs. See `Assets/Social/UPSTREAM.json` for their sources. The game and platform artwork belongs to the respective rights holders.

## Protocol research

The API, CRC64, and local metadata implementations were written for this project based on the user-supplied network
capture and inspection of the installed official launcher. No user authentication data or account session is required or
distributed. The application-level request signature uses the public constant shipped in the official launcher, rather
than a user account credential.
