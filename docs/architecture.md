# Architecture and verified protocol

## Windows distribution

The NativeAOT DLL exports Collapse Plugin Core API 0.1.6. `StellaSoraRegion` defines the launcher identity, API, executable, language, and game data location for each store-independent Windows client.

| Region | Official launcher inspected | Game executable | Default game directory | Unity company |
| --- | --- | --- | --- | --- |
| CN | [1.3.0](https://stellasora.yostar.cn/) | `xtlr.exe` | `YostarGames/StellaSora_CN` | RoamingStar |
| EN | [1.6.0](https://stellasora.global/) | `StellaSora.exe` | `YostarGames/StellaSora_EN` | YOSTAR (HONG KONG) LIMITED |
| JP | [1.6.0](https://stellasora.jp/) | `StellaSora.exe` | `YostarGames/StellaSora_JP` | Yostar, Inc. |
| KR | [1.6.0](https://stellasora.kr/) | `StellaSora.exe` | `YostarGames/StellaSora_KR` | YOSTAR (HONG KONG) LIMITED |
| TW | [1.8.2](https://stellasora.stargazer-games.com/) | `StellaSora.exe` | `StargazerGames/StellaSora_TW` | Stargazer HongKong Limited |

The launchers start the listed executable and the game handles account login. TW supports launcher-supplied arguments. Its current package advertises an empty argument list. Steam uses the EN account service and manages its own distribution. [Official Steam FAQ](https://stellasora.global/news/4896)

## API flow

| Endpoint | Purpose |
| --- | --- |
| `/api/launcher/base/config` | Background and launcher information |
| `/api/launcher/game/config` | Base package version, basis path, and executable name |
| `/api/launcher/game/config/json?version=...&file_path=...` | Locate the file manifest |
| `/api/launcher/advanced/game/download/cdn` | Primary and backup download origins |
| `/api/launcher/operations/resource` | Categorized news and time-limited banners |
| `/api/launcher/social/media/resource` | Website, social links, and QR resources |

API requests generate the public application-level signature for the selected region and current Unix time. The header is scoped to API requests and is not attached to manifest, artwork, or game file requests. No user account credential or captured session is used. TLS certificate validation remains enabled.

The game configuration's `size`, `file_url`, and `crc64` can be empty. The installer resolves the manifest through the locator endpoint instead of treating those fields as a downloadable archive. A manifest contains a `source` prefix and `file` entries with `path`, decimal CRC64 `hash`, and decimal `size`. Leading `/` in official entry paths is a package-relative convention, not a filesystem root. File URLs combine the CDN origin, manifest source, and escaped path segments.

The manifest request sends `Cache-Control: no-cache` and adds the launcher's `nocache` timestamp. On 2026-11-19, all five APIs listed base package 2.0.0. CN, EN, JP, KR, and TW listed 9,112, 9,254, 9,251, 9,254, and 9,255 files respectively.

## Installation and recovery

1. Acquire the installation lock and require the game and official launcher to be closed.
2. Refresh the release, fetch the manifest, validate paths, and hash existing target files.
3. Stage missing or invalid files in `.collapse-stellasora/cache/`. Each cache key includes both file identity and target path, so identical-content files do not race over a shared staging path.
4. Copy reusable official `.tmp` files into the plugin cache. Resume with HTTP Range; validate 206 Content-Range, restart on 200 responses that ignore Range, and reject mismatched lengths. Retry using the backup CDN when downloading or CRC64 verification fails. Respect the host speed limiter and cancellation.
5. Once all staged files verify, recheck running processes and write `pending.json`. Replace target files, remove only obsolete paths from a validated previous official manifest, then atomically write the official manifest and config. Remove the pending marker last.

Cancellation during download leaves the current installed version and resume cache intact. Cancellation or failure during commit leaves a pending marker; a future operation rehashes the actual files and resumes safely. The implementation does not promise rollback of already replaced files. No completed version is inferred from file presence or the latest API version.

The official metadata uses Base64 MD5 over semicolon-joined object values in insertion order, excluding `vc` and the manifest's `files` array. TW includes a `params` array, whose elements are joined by commas, and a `gameUninstallScript` string. The installer checks the config, manifest, and pending commit for a different region before changing game files.

Path validation rejects traversal, drive/UNC paths, Windows device names, alternate data streams, metadata collisions, case-insensitive duplicates, and file/directory conflicts. Writes reject reparse points. Uninstall removes only paths from a validated regional manifest and preserves unrelated files.

## Responsiveness and progress

Size-query entry points always dispatch work to the thread pool, including when the manifest is already cached. Estimates use one cached file-info lookup per path instead of repeating full ancestor validation. Visible verification uses an operation-scoped directory validation cache and slicing-by-eight CRC64, and reports both bytes and actual file counts. Core's StateCount and TotalStateToComplete represent completed/total verification items in this phase, rather than installation phase numbers. Official partial-file copies use cancellable asynchronous reads/writes and contribute byte progress to the download phase.

## UI and maintenance

CN, JP, and TW publish news in `news_list`. EN and KR publish date-only notices in `notice_list`. Categorized entries take precedence over duplicate entries from the latest tab. The plugin filters banners by their start and end times and falls back to the region's default background when refresh fails.

The maintenance exports check the active manifest. Repair uses the same installer and can update the base package. Reports go outside the game directory.

## Validation boundaries

The console regression harness exercises real filesystem staging and HTTP semantics using isolated fixtures. The optional live mode validates the production signature and manifest and installs two actual CDN sample files into `.dev/tests/`. Native smoke checks validate exported APIs, COM preset creation, en/zh metadata, and the embedded icon. Package checks validate declared paths, sizes, and MD5 checksums.

These checks do not establish full-game installation, successful Collapse UI import, in-game login, anti-cheat behavior, or future region compatibility.
