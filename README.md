# EV

One simulated electric vehicle: what it is, what its battery wants, how it
finds a charging station, and the web interface all of that is read and changed
through.

This is the library. The program that starts it is
[EVCLI](https://github.com/OpenChargingCloud/EVCLI), and the split is the same
one the charging station makes: the command line is a vocabulary of switches
and belongs to whoever starts the thing; everything a vehicle *is* lives here.


## What is in it

| | |
|---|---|
| `EV.cs` | the vehicle: its clock, its log, its HTTP server, and one SDP discovery at a time |
| `EV.Session.cs` | charging: one session at a time, and the handle a pause leaves behind |
| `EV.Configuration.cs` | what the Configuration pages read and write - DNS, NTS, the battery, the link, the certificates, the session |
| `EV.Clock.cs` | what time it thinks it is, and what that is worth |
| `EV.Diagnostics.cs` | asking a name server or a time server something, step by step |
| `HTTPAPI/EVHTTPAPI.cs` | the JSON API at `/api`, and the Server-Sent Events stream everything travels on |
| `ISO15118/V2GLink.cs` | the wire below the charging cable: which interfaces could carry it, SLAC, the 10BASE-T1S bus of an MCS coupler, and the SDP client |
| `ISO15118/V2GSession.cs` | one session, from the TCP connection to `SessionStop` |
| `ISO15118/VehicleCredentials.cs` | the credentials a session was given, turned into the shapes it needs - and checked against the roots this vehicle believes |
| `Certificates/` | the store: what a certificate is for, what may go in, and what survives a restart |
| `Configuration/` | one record per section of the configuration file |
| `Web/` | who may sign in, what each role may do, and the session cookie |
| `Logging/` | one log for everything, in memory and on the stream |
| `Frontend/` | the web interface: TypeScript and SCSS, bundled by webpack and embedded into the assembly |


## The web interface is part of the assembly

`EV.csproj` runs `npm run build` in `Frontend/` and embeds `Frontend/dist` as
manifest resources named `cloud.charging.open.EV.HTTPRoot.*`, which is what
Hermod's `EmbeddedContentSource` reads. So a build needs Node.js, and a
deployment needs nothing but the DLL.

`dotnet build -p:SkipFrontendBuild=true` leaves the npm step out and reuses
whatever is already in `dist/`.


## Nothing goes out on the link by itself

A charging station answers SDP because it is a charging station: its listener
is up for the lifetime of the process. A vehicle's side is not. It multicasts
when somebody plugs a cable in or presses a button, and has nothing to say
until the next time — which is why `V2GLink` is a handful of operations rather
than a running object, and why there is no `Start` on it.

One discovery at a time, one pairing, one session, and a second request is
refused rather than queued. For discovery the reason is the socket: two
discoveries would put two questions on one link and sort the answers between
them by arrival order, which is not sorting them at all. For a session the
reason is simpler — a vehicle has one cable, and two sessions would be two
vehicles, the second one charging through the first one's battery.

Each stage can also be asked on its own, and that is what `--slac`, `--t1s`
and `--sdp` do. `AttachToBusAsync` is the one for the 10BASE-T1S bus below a
megawatt coupler: a session joins that bus by itself where one is configured,
so this exists for the same reason the pairing stage has its own entry point —
joining and then finding no station over SDP is a different link from never
being given a node identifier at all, and only asking the two questions
separately tells them apart. It stays on the bus for two seconds before
leaving, so the station's log shows a node that was asked and answered rather
than one that came and went inside a single cycle.

It answers rather than throws where it cannot run: `notConfigured` when no
T1S transport has been named, `busy` while a session holds the bus. Both are
states somebody can be in on purpose, and neither is a fault.


## The session is somebody else's code

`V2GSession` is wiring and running commentary. The behaviour is in
`WWCP_ISO15118_Session` — `Evcc2` for ISO 15118-2 and the `Evcc20*` family for
-20 — which is the same code the conformance harnesses drive and the same code
the station's side is written against. What this adds is what those state
machines do not produce: which protocol the handshake settled on, what the TLS
layer turned out to be, and what the run added up to.

**It writes into the log rather than returning a transcript.** A session is
hundreds of exchanges and a full charge is minutes, so the question somebody
has while one runs is "where is it now" — which only the Logs page and the
event stream can answer. The result that comes back at the end is the sum.

Which is also why `POST /api/v1/session` answers *before* the session is over.
A request held open for a full charge is a request that times out; the vehicle
says it has started, and the session resource carries the result when it ends.


## Certificates, and where they live

Everything this vehicle believes and everything it presents is in one store —
`Certificates/CertificateStore.cs`, a directory of files with an `index.json`
beside them — and is addressed by a short handle rather than by a path.

Two groups, and they behave differently in every respect that matters. A
**root** is what this vehicle believes: `v2gRoot` for the station's chain,
`moRoot` for a contract's, `oemRoot` for a provisioning chain. Any number of
each may be switched on at once, all of them are believed, and none is ever
chosen for a session. Keeping the three apart is the point — one bag of roots
would let an OEM root vouch for a contract, which is the difference between a
vehicle that checks who is charging it and one that checks that somebody signed
something.

A **credential** is what this vehicle presents: the Vehicle certificate is who
it is, the contract certificate is who pays, the OEM provisioning certificate
is what it was born with, and the tariff certificate is what a station's signed
tariff is checked against. Exactly one of each is chosen, and that choice is a
session setting — `SessionConfiguration` carries the handle, never the file.

The store holds private keys **unencrypted**: a PKCS#12 is opened with its
password once, at import, and written back without one, so that any number of
certificates per role work without any number of passwords to carry. The file
system is what guards them, and the vehicle says so at every start and at every
import.


## What it is not

A car. Chains are validated only when trust roots say so and revocation never
is; the message timeout is a flat two seconds rather than the standard's
performance tables; and there is no electrical layer at all. The battery is
arithmetic on a simulated clock — linear below the taper knee, no temperature,
no losses, no ageing — so a run that ends "at 100 %" is reporting a sum and not
a charging curve.
