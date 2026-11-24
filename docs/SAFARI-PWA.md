# Safari / PWA in this fork

[Back to PhoneBox](../README.md) · [Privacy](../PRIVACY.md)

This fork can show the existing PhoneBox panel in Safari or as an iPhone Home Screen web app. It uses a
separate HTTPS listener and browser pairing; it does not require an Android phone, an App Store app,
Apple Developer membership, or app signing. The setup below targets a Mac and an iPhone 13.

Use a build of this fork that includes Safari / PWA. The upstream download and setup scripts do not install
this feature. For a source build, follow [Development](DEVELOPMENT.md#build-and-run); Android build tools
are not needed for the browser panel. Keep automatic upstream updates off while using a fork-only build,
so an upstream release does not replace it.

## 1. Enable the panel on the Mac

1. Open PhoneBox settings at <http://127.0.0.1:8765> on the Mac.
2. Under **General → Safari / PWA**, select **Enable the HTTPS browser panel**.
3. Set **Mac hostname or LAN address** to the Mac's reachable `.local` hostname, such as `your-mac.local`.
   On macOS, `scutil --get LocalHostName` prints the name before `.local`. Keep the default **HTTPS port**
   `8767`, or choose another unused port. The settings port `8765` and Android Wi-Fi port `8766` are separate.
4. Click **Save**, then **Restart to apply**. The section shows the panel URL and certificate fingerprint
   when the listener is active. Keep the Mac awake and connected to a network the iPhone can reach.

Use the URL shown in settings, for example `https://your-mac.local:8767/kiosk/`. The browser listener binds
to network interfaces; local administration remains on `127.0.0.1:8765`. Do not expose either service
through router port forwarding. If `.local` does not resolve on your network, use a LAN address listed by
the active listener. A changed hostname or address may require saving and restarting to renew its certificate.

## 2. Trust this Mac's certificate on the iPhone

1. On the Mac, click **Download certificate profile**. Transfer `PhoneBox-Mac.mobileconfig` directly to
   the iPhone with AirDrop. The profile contains only this installation's public certificate authority (CA).
2. On the iPhone, open **Settings → Profile Downloaded**, review the certificate details and compare its
   SHA-256 fingerprint with the one shown in PhoneBox settings on the Mac. Install only the matching profile.
   Installed profiles are listed under **General → VPN & Device Management**.
3. Open **Settings → General → About → Certificate Trust Settings** and enable full trust for the matching
   **PhoneBox … Local CA**. Manually installing a profile does not enable SSL trust automatically.
4. Open the panel's HTTPS URL in Safari. Resolve any certificate or hostname error before pairing.

This creates a system trust entry on the iPhone. The CA's private key remains in the Mac's PhoneBox data
directory; it is never included in the profile. Remove the PhoneBox profile when you stop using this Mac's
panel. See Apple's [profile installation](https://support.apple.com/en-us/102400) and
[certificate trust instructions](https://support.apple.com/en-us/102390).

## 3. Pair Safari and add it to the Home Screen

1. In the Mac's **Safari / PWA** section, leave **Allow media, volume and configured shortcuts** unchecked
   for monitoring only. If needed, select it before creating this browser's invitation.
2. Click **Pair an iPhone / browser**. Scan the QR code or open its pairing link on the iPhone. The invitation
   is single-use and expires after five minutes; generating another invalidates the previous one.
3. Give the browser a recognizable name and tap **Connect**. The code is carried in the URL fragment,
   removed from the address bar by the pairing page, and submitted to the Mac only when you connect.
4. To install the panel, open its `/kiosk/` address in Safari and choose **Share → Add to Home Screen**.
   Enable **Open as Web App** if offered. Open the new icon; if it asks to pair, generate a new invitation
   on the Mac and enter its code there. Safari and the installed web app can have separate sessions.

Each pairing gets its own **30-day** secure, HttpOnly, SameSite=Strict cookie. Access can be revoked in the
Mac's device list, or from **Connection → Disconnect this browser** in the panel. Expiration, revocation,
or clearing the browser's site data requires pairing again. Changing between a hostname and an IP address
also uses a different cookie origin. See Apple's [Home Screen web app guide](https://support.apple.com/guide/iphone/iphea86e5236/ios).

## Layout, controls, and AI quotas

Edit the layout under **Pages** on the Mac. The browser reuses that panel and its existing widgets; swipe
or tap page dots to navigate. The browser shell follows English or Brazilian Portuguese browser preferences.
The pairing shell supports these two languages. The Mac's browser-panel and ai-usagebar settings use the
shared translation system and support the existing 23 interface languages.

The top controls hide after five seconds without interaction. Tap the top edge of the panel to show them
again, or use Tab with a keyboard. Controls remain visible while connection details are open, while they
have keyboard focus, and during connection errors. The invisible reveal target draws no permanent icon;
this reduces static screen content but does not guarantee protection against OLED burn-in.

To stay on the page you selected, set **General → Page switching → Back home after** to `0` and save.
A page-specific idle timeout in the page editor overrides that global setting.

- Monitoring includes available computer metrics, media information, quota readings, and reduced AI session
  summaries. Browser sessions do not receive conversation text, project paths, permission prompts, or session controls.
- Optional controls allow media playback, seeking, output volume, and shortcuts already configured on the Mac.
  Review those shortcuts before granting access: a configured shortcut can run its action on the computer.
- The browser cannot edit settings, approve AI permissions, kill processes, or use command-output, custom HTML,
  camera, spectrum, app-console, dictation, phone battery, charge-care, or computer-switch widgets.
- For existing **ai-usagebar** accounts, enable **General → AI quotas & CodexBar → Read accounts from ai-usagebar**
  (`codexBar.aiUsageBar: true`). The executable must be installed on the Mac; set its absolute path if it is
  not found on `PATH` or at `~/.cargo/bin/ai-usagebar`.
- In a quota widget's provider list, use the report's account IDs, for example `claude@me`, `claude@b3`, `codex`,
  and `minimax`. The adapter normalizes `anthropic` / `anthropic-desktop` to `claude` and `openai` to `codex`,
  retaining account suffixes. These are examples, not automatically created accounts. An unconfigured MiniMax
  entry shows a configuration placeholder, not a zero usage reading.
- SuperGrok, read from the Grok CLI sign-in, uses the account ID `supergrok` and shows one ring for its weekly
  limit. Its per-product rows, such as "xAI API", are slices of that weekly limit (report group `Breakdown`) and
  are not drawn as separate rings.

The agent runs `ai-usagebar usage --json` about once a minute while the integration and a quota consumer are
enabled: a quota widget, a clock status line, or a quota automation rule. This is cache-first, not guaranteed
offline: the CLI may refresh its configured providers using its
own credentials. PhoneBox receives selected quota fields; it does not import those credentials. Missing,
failed, or stale readings are marked accordingly. Provider setup and sign-in remain in ai-usagebar.

## Behavior and validation limits

**Keep screen awake** is optional. Home Screen web apps need iOS 18.4 or later for this API; Low Power Mode,
locking the phone, or switching apps can release it. Return to the panel and enable it again if necessary.
See the [Safari 18.4 release notes](https://webkit.org/blog/16574/webkit-features-in-safari-18-4/).

The panel needs a running, reachable Mac. After reconnecting or returning to the foreground, it checks the
session again. Widget data and configuration use volatile memory in browser mode, and API responses are
not cached. The service worker supplies only an offline notice; it does not store dashboard data for offline use.

Desktop browser checks do not establish physical iPhone compatibility. Before relying on the panel, verify
certificate installation, HTTPS access, pairing, Home Screen launch, foreground reconnect, revocation, and
screen-awake behavior on the actual iPhone and iOS version. Media and volume support also depend on macOS
tools and the selected audio device, as described in [Platform support](../README.md#platform-support).
