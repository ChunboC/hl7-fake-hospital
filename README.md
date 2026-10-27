# Hopital

A fake hospital for testing HL7 v2 integrations. Hopital plays the **Epic side** of an
interface: it sends registrations, orders and schedules over MLLP, and it receives results
(ORU, MDM) the way Epic would, with a switch to make it accept, reject or go offline.

It is a .NET 10 console tool that runs on Windows and on a Raspberry Pi 4.

**All data is made up.** Names come from built-in lists, MRNs and order numbers are random,
phone numbers use the 555-0100 to 555-0199 range reserved for fiction, and no SSN is ever
produced. Never put real patient data in scenarios or samples.

## Contents

- [How it fits together](#how-it-fits-together)
- [Quick start on Windows](#quick-start-on-windows)
- [Commands](#commands)
- [Scenarios](#scenarios)
- [The fake Epic receiver](#the-fake-epic-receiver)
- [The web page](#the-web-page)
- [Raspberry Pi setup](#raspberry-pi-setup)
- [Deploying to the Pi](#deploying-to-the-pi)
- [Open Integration Engine](#open-integration-engine)
- [Project layout](#project-layout)

## How it fits together

Messages from Epic usually reach the receiving system through an interface engine; here that is
Open Integration Engine (OIE), running next to the system under test. Some hospitals also run
their own engine in front of it. The Pi plays the hospital; the PC plays the receiving side.

```
 Pi "Hopital" (the hospital)                     PC (receiving side)   
 ┌──────────────────────────────┐                ┌──────────────────────────────────┐
 │ hopital run (fake Epic out)  │── MLLP 6661 ──►│ OIE ──► system under test         │
 │                              │                │  │                                │
 │ hopital receive (Epic in)    │◄── MLLP 6662 ──│  └── results (ORU, MDM, images)   │
 │   web page HTTP 6680         │                └──────────────────────────────────┘
 │                              │
 │ optional: a second OIE as    │   Sites with their own engine:
 │ the hospital's own engine    │   Epic ──► hospital engine ──► the PC's OIE
 └──────────────────────────────┘
```

| Port | Where | What |
|------|-------|------|
| 6661 | PC (OIE) | MLLP in: ADT, ORM, SIU from Epic |
| 6662 | Pi | MLLP in: results sent back to the fake Epic |
| 6680 | Pi | HTTP: the [web page](#the-web-page), the receiver's mode and status, the API |

All ports can be changed on the command line.

## Quick start on Windows

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). From the repository folder:

```powershell
dotnet test                                             # 132 tests, about 5 s
dotnet run --project src/Hopital -- generate --count 5   # preview the fake data
```

Try the sender and receiver against each other in two terminals:

```powershell
# Terminal 1: a receiver that saves everything and answers AA
dotnet run --project src/Hopital -- receive --port 6662

# Terminal 2: send a sample result, then run a scenario against the same port
dotnet run --project src/Hopital -- send samples/results/oru-r01-embedded-images.hl7 --port 6662
dotnet run --project src/Hopital -- run scenarios/smoke-test.yaml --port 6662
```

While the receiver runs, open <http://localhost:6680/> for the [web page](#the-web-page).

Use `--dry-run` with `run` to print every message without sending anything.

While a `receive` or `listen` started with `dotnet run` is still running, a new build cannot
replace `Hopital.exe`. If a build says the file is locked, stop it with Ctrl+C first.

## Commands

On Windows run them as `dotnet run --project src/Hopital -- <command>`; on the Pi just
`hopital <command>`. Every command has `--help`.

| Command | What it does |
|---------|--------------|
| `run <scenario.yaml>` | Sends a scenario's messages in order, waits for each ACK, checks it, prints a summary. `--host`, `--port` (default 6661), `--dry-run`. Exit code 0 only if every message got the ACK it expected; 2 if the scenario file has problems. |
| `send <file.hl7>` | Sends one message from a text file (one segment per line) and prints the ACK. `--host`, `--port`, `--timeout`. |
| `receive` | The fake Epic receiver: saves every message and answers according to its mode. `--port` (6662), `--out` (`received`), `--mode`, `--control-port` (6680, 0 = off; also serves the web page), `--scenarios` (the folder the web page runs from: `./scenarios`, else the one next to the program), `--bind`. |
| `mode <mode>` | Switches a running receiver: `accept`, `error`, `reject`, `hang`, `drop`, `offline`, or `status`. `--messages N`, `--for 2m`, `--text "..."`, `--host`, `--control-port`. |
| `listen` | A plain listener that prints every message and answers AA. Handy for a quick look. `--port` (6662). |
| `generate` | Prints made-up patients and orders. `--count`, `--seed`. |
| `samples` | Writes one example message per type to `samples/` (A04, A08, ORM new/in progress/cancel, S12, A40). `--seed`, `--version` (e.g. `2.3`). |

## Scenarios

Scenarios are YAML files in [scenarios/](scenarios/). [smoke-test.yaml](scenarios/smoke-test.yaml)
has a comment on every field; this is the short version:

```yaml
name: Smoke test
seed: 1                    # same seed, same made-up patients and orders
patients: 3                # patients 1 to 3; patient N has order N
duplicates: { 4: 1 }       # patient 4 = patient 1 again under a new MRN (for merges)
spacing: 500ms             # wait between messages
ackTimeout: 10s
header: { version: "2.5.1" }   # MSH overrides: applications, facilities, version, processing ID
steps:
  - forEachPatient:            # repeated for every patient
      - send: adt-a04
      - send: orm-new
  - wait: 1s
  - send: orm-cancel
    patient: 3
    delay: 2s                  # replaces spacing for this message
    expect: AA                 # AA (default), AE, AR, any, or none (no ACK expected)
```

**Sends:** `adt-a04`, `adt-a08` (new address and phone), `adt-a40` (with `merged:`),
`orm-new`, `orm-cancel`, `orm-status` (with `status: IP`), `siu-s12` (with `minutes:`),
`resend` (the previous message again, byte for byte, like Epic retrying), and `raw`
(any `text:`, with `{controlId}` and `{now}` filled in).

**On any send:** `controlId:` (reuse an MSH-10), `label:`, `framing:` (`no-trailing-cr`,
`junk-before`, `none`, `no-end-block`).

A misspelled key is an error with its line number, and every problem in a file is reported
together with its step number, before anything is sent.

| Scenario | Messages | Takes | Tests |
|----------|---------:|------:|-------|
| [clinic-day](scenarios/clinic-day.yaml) | 64 | ~3 min | 20 patients registered, ordered and booked, then an update, a cancel, IP and CM |
| [duplicate-message](scenarios/duplicate-message.yaml) | 6 | 14 s | Exact resends of an order; a different message reusing a control ID |
| [cancel-after-start](scenarios/cancel-after-start.yaml) | 5 | 19 s | An order cancelled after the procedure started (ORC-5 IP) |
| [merge](scenarios/merge.yaml) | 6 | 13 s | Same person under two MRNs, each with an order, merged with A40 |
| [malformed](scenarios/malformed.yaml) | 12 | 21 s | Bad content and broken MLLP framing, then a good message |
| [burst-500](scenarios/burst-500.yaml) | 500 | seconds | 500 messages back to back |
| [schedule-only-day](scenarios/schedule-only-day.yaml) | 20 | 38 s | A site that sends SIU instead of orders |
| [smoke-test](scenarios/smoke-test.yaml) | 11 | 8 s | A quick end-to-end check of a new setup |

Steps marked `expect: any` are where the right answer is still a decision for the system under test
(for example, what to answer a cancel after the procedure started). Change them to `AE` or
`AR` once decided, and the scenario becomes a regression test.

## The fake Epic receiver

`receive` saves every message it gets and answers according to its mode:

| Mode | The sender sees |
|------|-----------------|
| `accept` | AA |
| `error` | AE with text in MSA-3 and ERR: a temporary problem, retry later |
| `reject` | AR with text: refused, do not retry |
| `hang` | The message is read but never answered, so the sender times out |
| `drop` | The connection is closed without a reply |
| `offline` | The port is closed: connections are refused, open ones are cut |

Any mode can be temporary, then it goes back to `accept` on its own:

```powershell
dotnet run --project src/Hopital -- mode error --messages 3 --text "Chart is locked" --host hopital.local
dotnet run --project src/Hopital -- mode offline --for 2m --host hopital.local
dotnet run --project src/Hopital -- mode status --host hopital.local
```

The same with curl (in PowerShell type `curl.exe`, not `curl`):

```powershell
curl.exe -X POST "http://hopital.local:6680/mode/error?messages=3&text=Chart%20is%20locked"
curl.exe http://hopital.local:6680/status
```

`GET /help` on the same port lists every endpoint.

In an interactive terminal the receiver also takes single keys: `a` `e` `r` `h` `d` `o`, and
`s` for status.

The control endpoint has **no login**. It is meant for a private test network only.

**What is saved**, in `received/<date>/`:

- `<time>_<type>_<control ID>.hl7`: the message exactly as received.
- `.json` next to it: message type, control ID, MRN and name, order, accession and document
  numbers, the ACK sent, the mode at the time, and any framing warnings.
- Embedded images and documents (OBX value type `ED`, Base64, Hex or plain text) are decoded
  into their own files: `_obx2.png`, `_obx1.pdf`, and so on.
- References (OBX `RP`, or a URL) are listed in the JSON. They are not downloaded.
- Embedded data that does not decode is listed in the JSON and the log instead.

[samples/results/](samples/results/) has example results to try it with: an ORU with two
embedded PNGs, an ORU with an image reference, an MDM T02 with a PDF report, and an ORU
with broken image data.

## The web page

The receiver also serves a page on its HTTP port: <http://hopital.local:6680/> (or
<http://localhost:6680/> on the PC). Everything on it updates live, and several people can
have it open at once.

- **Send a scenario:** pick one of the scenarios, enter the host and port to send to, and
  press **Run**. Each message's result appears as it happens, with a progress bar, a filter
  for the messages that did not get the expected answer, and the same summary as
  `hopital run`. **Stop** ends a run and keeps what was sent. **Preview messages** shows
  every message without sending anything. The host, port and scenario are remembered in
  that browser.
- **Receiver:** the current mode, with buttons for every mode. A mode can last until you
  change it, for the next N messages, or for a length of time, with optional AE/AR text.
- **Received:** the newest 100 messages. Click one to see its details, the segments (point
  at a field for its name, click it for its components), embedded images, PDFs and other
  files, linked references (not fetched), and the ACK we sent. The address bar then links
  to that message.

Things to know:

- The scenarios run **from the Pi**, so the host must be reachable from the Pi: the PC's OIE's
  address on the PC (e.g. `192.168.1.50`), not `localhost`. To try a scenario against the
  Pi's own receiver, use host `localhost` and port `6662`.
- Only one run at a time, for everyone using the page. A second **Run** is refused until
  the first finishes or is stopped.
- The page runs the `.yaml` files in the receiver's scenarios folder (`~/hopital/scenarios`
  on the Pi, replaced by every deploy). Scenarios are edited in the repository, not on the
  page.
- Like the control endpoint, the page has **no login**: private test network only.
- Images and documents come from whoever sent the message. HTML, XML and other files that
  could run script are served sandboxed so they cannot act on the page.

The page uses this API, which also works from scripts:

| Request | What it does |
|---------|--------------|
| `GET /api/scenarios` | The scenario files, with message counts and any mistakes |
| `POST /api/runs` | Start a run: `{"scenario": "smoke-test.yaml", "host": "192.168.1.50", "port": 6661}`. Add `"dryRun": true` to get the messages back without sending. 409 if a run is going. |
| `GET /api/runs/current` | The latest run: state, counts, every result, the summary |
| `POST /api/runs/current/stop` | Stop it |
| `GET /api/received?limit=50` | The newest received messages (up to 500) |
| `GET /api/received/<day>/<name>` | One message's summary and text |
| `GET /api/files/<day>/<file>` | A saved file: the `.hl7`, its `.json`, or an attachment |
| `GET /api/events` | Live updates as server-sent events: `mode`, `received`, `run`, `result` |

## Raspberry Pi setup

These steps set up a Raspberry Pi 4 Model B with no screen or keyboard, reached over SSH.
2 GB of RAM is plenty for Hopital alone; 4 GB also fits an OIE on the Pi.

### 1. Write the SD card

1. Install [Raspberry Pi Imager](https://www.raspberrypi.com/software/) on the PC.
2. **Device:** Raspberry Pi 4. **OS:** Raspberry Pi OS (other) → **Raspberry Pi OS Lite (64-bit)**.
   It must be the 64-bit version; Hopital is built for `linux-arm64`.
3. **Storage:** the microSD card (16 GB or more). Everything on it is erased.
4. When asked about OS customization, choose **Edit Settings**:
   - **General:** hostname `Hopital`, a user name and password, your time zone.
     For Wi-Fi, tick *Configure wireless LAN* and set the SSID, password and **country**
     (Wi-Fi stays off if the country is wrong). Skip it if you use Ethernet.
   - **Services:** enable **SSH** with password authentication.
5. Write the card.

### 2. First boot

Put the card in the Pi (slot on the underside), connect Ethernet if you use it, then power
(USB-C, 5 V 3 A; phone chargers often cause crashes). There is no power button. The first
boot takes about 2 minutes.

```powershell
ssh pi@hopital.local     # use your Pi user name
```

If `hopital.local` is not found, look up the Pi's address in your router's device list and
use that instead (`-PiHost 192.168.x.x` for the deploy script). A DHCP reservation in the
router keeps the address from changing.

Then, on the Pi:

```bash
free -h                              # check the RAM
sudo apt update && sudo apt full-upgrade -y
```

Shut down with `sudo shutdown -h now` and wait for the green light to stop before
unplugging. Pulling the power while it runs can corrupt the card.

### 3. Log in without a password (recommended)

Without this, every deploy asks for the Pi password twice. On the PC, in PowerShell:

```powershell
ssh-keygen -t ed25519            # once; press Enter to accept the defaults
type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh pi@hopital.local "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"
ssh pi@hopital.local         # should log in without asking
```

`sudo` on the Pi may still ask for the password while installing the service. That is
expected.

### 4. Firewalls

Raspberry Pi OS Lite has no firewall turned on, so ports 6662 and 6680 are reachable
right away. On the **PC**, Windows Firewall blocks incoming connections by default, so allow
OIE's port once the Pi needs to send to it (PowerShell as administrator):

```powershell
New-NetFirewallRule -DisplayName "OIE MLLP 6661" -Direction Inbound -Protocol TCP -LocalPort 6661 -Action Allow
```

## Deploying to the Pi

From the repository folder on the PC:

```powershell
powershell -ExecutionPolicy Bypass -File deploy\deploy.ps1 -User pi
```

[deploy/deploy.ps1](deploy/deploy.ps1):

1. Builds a self-contained, single-file `linux-arm64` program (about 47 MB). The Pi does not
   need .NET installed.
2. Copies it, `scenarios/` and `samples/` to the Pi with `scp`.
3. Runs [deploy/install.sh](deploy/install.sh) on the Pi over `ssh`, which installs into
   `~/hopital`, links `hopital` onto the PATH, and installs and restarts the
   [hopital-receiver](deploy/hopital-receiver.service) systemd service.

Options: `-PiHost <name or IP>` (default `hopital.local`), `-NoService` (install only),
`-BuildOnly` (just build, into `artifacts\deploy\hopital`). Set `HOPITAL_PI_USER` to skip
`-User`.

Run the same command again after any change; it replaces the program and restarts the
service.

Then open <http://hopital.local:6680/> from any computer on the network.

### Running things on the Pi

```bash
# The receiver service
sudo systemctl status hopital-receiver
journalctl -u hopital-receiver -f          # follow the log (Ctrl+C to stop)
sudo systemctl restart hopital-receiver

# The sender: run a scenario against the PC's OIE on the PC (use the PC's IP address)
hopital run ~/hopital/scenarios/clinic-day.yaml --host 192.168.1.50 --port 6661
```

To copy what the receiver saved back to the PC:

```powershell
scp -r pi@hopital.local:hopital/received .\received-from-pi
```

## Open Integration Engine

[Open Integration Engine](https://github.com/OpenIntegrationEngine/engine) is the open-source
fork of Mirth Connect. Hopital does not depend on it, but it is what sits between Epic and
the receiving system in many hospitals, so tests should go through it.

Details change between OIE releases. The notes below are for 4.7.0; check the
[release notes](https://github.com/OpenIntegrationEngine/engine/releases) for the version you install.

### Next to the server (the normal setup)

1. Download the installer for your OS from the
   [releases page](https://github.com/OpenIntegrationEngine/engine/releases). The
   `-with-JRE` installers include Java; the others need Java 17 or later already installed.
2. Install and start the server. From 4.7.0 a new install **no longer uses admin/admin**:
   either set `server.initialadminpassword` before the first start, or read the generated
   password from the log of the first start.
3. Open the server's landing page in a browser (by default the HTTPS port set during
   installation). It links to the Administrator launchers; install one and log in.
4. Create two channels:

   **Epic in** (receives the fake Epic's ADT, ORM and SIU)
   - Source: *TCP Listener*, port **6661**, transmission mode **MLLP**,
     response: auto-generate the ACK (for example *After source transformer*).
   - Destination: if the system under test has no listener yet, a *TCP Sender* to
     `hopital listen --port 6663` running on the PC works as a stand-in, or a *File Writer*.

   **Results out** (sends the system under test's results back to the fake Epic)
   - Source: whatever the system under test uses to hand results to OIE.
     To test now, a *TCP Listener* on another port, fed with
     `hopital send samples/results/oru-r01-embedded-images.hl7 --port <that port>`.
   - Destination: *TCP Sender* to **hopital.local:6662**, MLLP, wait for the response,
     and turn on **queuing with retries**. Then use `hopital mode error`, `hang` and
     `offline --for 2m` to watch OIE's retries and the message statuses.

5. Allow port 6661 through Windows Firewall (see [Firewalls](#4-firewalls)).

### Optional: OIE on the Pi as the hospital's own engine

Some sites put their own engine between Epic and the receiving side's. A second OIE on the Pi can play
that part: fake Epic → Pi OIE → the PC's OIE → the system under test. It needs a 4 GB Pi.

1. Install Java (the version in apt depends on the Pi OS release; any 17 or later works):

   ```bash
   apt search openjdk 2>/dev/null | grep -E '^openjdk-[0-9]+-jre-headless'
   sudo apt install -y openjdk-21-jre-headless      # or the version listed above
   ```

2. Download the Linux archive (`oie_unix_<version>.tar.gz`) from the
   [releases page](https://github.com/OpenIntegrationEngine/engine/releases) and unpack it,
   for example into `/opt/oie`.
3. Cap the Java heap at 1 GB so it leaves room for everything else. Put `-Xmx1024m` in the
   server's vmoptions override in `conf/` (`oieserver.vmoptions` in recent releases; check
   the file names in your install).
4. Start it with the server launcher in the install folder, and run it as a systemd service
   if you want it to survive reboots (the [hopital-receiver](deploy/hopital-receiver.service)
   unit is a template).
5. Make one pass-through channel: TCP Listener (MLLP) on, say, port 6671 → TCP Sender (MLLP)
   to the PC's OIE on the PC, port 6661. Then point the fake Epic at it:
   `hopital run <scenario> --host localhost --port 6671`.

Open the Pi OIE's Administrator from the PC, the same way as the one next to the server.

## Project layout

```
src/Hopital/
  Mllp/         framing (0x0B ... 0x1C 0x0D), client, listener
  Hl7/          message builders (ADT, ORM, SIU), ACK builder and parser, parsing, escaping
  Generation/   made-up patients, providers and orders (seeded)
  Scenarios/    YAML loading, checking and expanding, the runner
  Receiver/     fake Epic receiver: modes, message store, HTTP control and API
  Web/          the web page (index.html, built into the program), run manager, live events
  Program.cs    the commands
tests/Hopital.Tests/   xUnit tests
scenarios/             scenario files
samples/               example messages; samples/results/ has example ORU and MDM results
deploy/                deploy.ps1, install.sh and the systemd unit
```
