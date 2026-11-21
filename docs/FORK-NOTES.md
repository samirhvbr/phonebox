# Fork implementation notes

This development snapshot adds browser access and ai-usagebar support to PhoneBox. It is based on upstream
`086c91528419b54127aff6858eedd93a6abe96ed`, with the fork version recorded in `version.md`. There is no native
iOS application or packaged fork release. See [Safari / PWA setup](SAFARI-PWA.md) for source builds and pairing.

## Changes

- An independent, opt-in HTTPS browser listener, with a dedicated local CA and a public-only iPhone certificate
  profile. Settings remain loopback-only and native Android pairing remains separate.
- Single-use invitations, hashed browser credentials, 30-day session expiration, and persistent per-browser
  revocation. Pairing grants monitoring by default; optional controls use existing configured action IDs.
- A browser API allow-list, reduced AI-session metadata, and no persistent layout or live-data cache. Browser
  clients cannot edit settings, approve AI prompts, read conversations, or terminate processes.
- Existing ai-usagebar named accounts projected into the existing quota widgets. Provider credentials stay with
  the CLI. Its cache-first report may still refresh configured providers; this is not an offline-only integration.
- A compact 45-pixel toolbar, excluding device safe areas, with 44-pixel touch targets. It hides after five idle
  seconds and can be restored by tapping the top edge or using Tab. Details, keyboard interaction, and connection
  errors keep it visible. This reduces static content without guaranteeing protection against OLED burn-in.
- A canonical `/kiosk/` redirect and explicit error MIME types, avoiding an empty download prompt in Safari.

Theme, weather location, idle timeout, account selections, certificates, and paired devices are per-install data.
No personal configuration, keys, certificate private keys, or account cache is included in this repository.

## Validation boundaries

Targeted backend tests cover pairing, replay, expiration, revocation, request origin/host policy, browser API
permissions, public projections, certificate renewal, and the ai-usagebar adapter. Browser tests run the real
assets against isolated API fixtures in Chromium and WebKit, including reconnection and toolbar interaction.

The focused checks passed: 31 browser-boundary backend cases, 56 quota-source and existing Codex/CodexBar cases,
12 browser-panel cases across Chromium and WebKit, and 20 shared translation/coverage checks. The final full
backend run passed 2,061 of 2,063 cases; its two failures were the same command-visibility timeouts reproduced
on unchanged upstream. These results do not claim that the complete upstream suites are green on this Mac.

The installed macOS ARM64 build was checked with the dedicated CA, without disabling TLS verification. Local
smoke checks covered pairing, allowed reads, forbidden routes, replay refusal, and logout/revocation. The owner
has connected an iPhone 13 and is testing the panel; desktop WebKit does not establish every physical iPhone
behavior. Home Screen session isolation, physical safe areas, and long-running screen-awake behavior still need
device acceptance.

The full upstream test suites have baseline failures on this Mac. An unchanged upstream UI run passed 354 of
387 tests and failed 33; an earlier fork run passed 357 of 390 and failed 33, with only five failures overlapping.
Those snapshots preceded the final toolbar tests and translation fixes, and do not establish a green final suite.
Two command-visibility backend tests also failed on the unchanged upstream base. Focused passing tests and
these baseline failures must be reported separately.

Java and the Android SDK are not installed on the validation Mac, so Android lint and APK assembly were not
run locally. Windows and Linux runtime behavior has not been manually tested.

## Upstream coordination

Upstream requests an issue first for new providers, integrations, or protocol changes. Its browser kiosk is
already reserved for in-house work in [#41](https://github.com/ersinkoc/phonebox/issues/41). This fork is a
prototype for its owner's use, not a claim to that task. Coordinate a small, independently reviewable contribution
before opening an upstream browser PR. The named-account integration is a separate proposal and must preserve
existing quota consumers and widget-specific provider choices.
