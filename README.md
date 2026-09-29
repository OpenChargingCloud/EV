# EV

One simulated electric vehicle: what it is, what its battery wants, how it
finds a charging station, and the web interface all of that is read and changed
through.

This is the library. The program that starts it is
[EVCLI](https://github.com/OpenChargingCloud/EVCLI), and the split is the same
one the charging station makes: the command line - the switches it is started
with, and the commands it can be typed at while it runs - belongs to whoever
starts the thing; everything a vehicle *is* lives here.


## What is in it

| | |
|---|---|
| `EV.cs` | the vehicle: what it adds to a node - its battery, its JSON API, and one SDP discovery at a time |
| `EV.Session.cs` | charging: one session at a time, and the handle a pause leaves behind |
| `EV.Configuration.cs` | what the Configuration pages read and write of the vehicle's own - the battery, the link, the certificates, the session |
| `VehicleAccess.cs` | who may do what: the vehicle's resources - `vehicle`, `v2g`, `session` - and its driver and its service |
| `HTTPAPI/EVHTTPAPI.cs` | what the vehicle adds to the JSON API every node answers at `/api` - its settings, V2G and the session; the rest, the Server-Sent Events stream everything travels on included, is WWCP_Node's `NodeHTTPAPI` |
| `ISO15118/V2GLink.cs` | the wire below the charging cable: which interfaces could carry it, SLAC, the 10BASE-T1S bus of an MCS coupler, and the SDP client |
| `ISO15118/V2GSession.cs` | one session, from the TCP connection to `SessionStop` |
| `ISO15118/VehicleCredentials.cs` | the credentials a session was given, turned into the shapes it needs - and checked against the roots this vehicle believes |
| `Configuration/` | one record per section of the configuration file that is the vehicle's: `vehicle`, `v2g`, `session` |
| `Frontend/` | the web interface: TypeScript and SCSS, bundled by webpack and embedded into the assembly |

Everything a running vehicle is before it is a vehicle - its log, its
configuration file, name resolution and the time, the certificate store, who
may sign in, and the HTTP server all of that sits behind - is not here. That
is [WWCP_Node](https://github.com/OpenChargingCloud/WWCP_Node), the part every
one of these programs shares, and `EV` is one `WWCPNode` with a battery: its
sections go into the same configuration file, and its routes into the JSON
API every node has below `/api` - the node's `NodeHTTPAPI`, with the
vehicle's settings, V2G and the session on top.


## The web interface is part of the assembly

`EV.csproj` runs `npm run build` in `Frontend/` and embeds `Frontend/dist` as
manifest resources named `cloud.charging.open.EV.HTTPRoot.*`, which is what
Hermod's `EmbeddedContentSource` reads. So a build needs Node.js, and a
deployment needs nothing but the DLL.

Not everything in that bundle is the vehicle's. What the web interface of every
kind of node shares - the frame with its menu, the sign-in, the Logs, DNS
client, NTS client and Certificates pages and the stylesheet, and below them
building HTML safely, the router, where the pages are mounted, asking before a
page's changes are left behind, how the node is asked and what every node
answers, the log the pages follow, and who is signed in - is WWCP_Node's, in
its `Frontend/src`, and bundled in as `@node/...`. What is the vehicle's is
what it adds: `main.ts` names its menu, its pages and its own words on the
Certificates page, `api/client.ts` its routes, and `styles/app.scss` its
colour. So a new WWCP_Node can change the vehicle's pages without a line
changed here, which is why the build counts those files among its inputs.

The vehicle's own pages are held to what every page of every kind of node is:
`src/pages/pages.test.ts` puts them under the rules of WWCP_Node's
`test/pages.ts` - a page with a form says whether it holds a draft and holds
every form it has, its Reload asks first, and it reads its numbers with
`numberField`, so that an emptied field goes out as "not given" and not as a
zero. Its links go through `toURL`, so that they stay below the base the
vehicle is mounted at; its look comes from the stylesheet, since a `style`
attribute is dropped by the policy the pages are served with; and what somebody
may not do, it says through `mayButNot`, which names no role as the one that is
needed - which roles there are, and what each may, is the configuration file's
to say. `npm test` runs it, and the CI with it.

And where a page draws itself anew - after one of its forms is saved, or a
discovery or a pairing has answered - what is typed into its other forms stays:
`src/drafts.ts` puts it back, and its test says what it keeps and what it
leaves as drawn.

`dotnet build -p:SkipFrontendBuild=true` leaves the npm step out and reuses
whatever is already in `dist/` - or, where nothing is, builds a vehicle with no
web interface at all: a warning rather than an error, because "no Node on this
machine" is a reason to build the backend alone. Such a vehicle answers on its
JSON API, serves a browser nothing, and says which of the two it is at every
start.


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


## Who may do what

Four roles, when the configuration file says nothing else, and what each may
do is an operation - `read`, `edit` or `run` - on a resource: the node's
`configuration`, `dns`, `nts` and `certificates`, and the vehicle's
`vehicle`, `v2g` and `session`.

| Role | may |
|---|---|
| `viewer` | read everything |
| `driver` | read everything; ask a name server, a time server or the link something; change what the battery wants and what a session asks for; charge |
| `service` | everything the driver may, and change where the vehicle resolves names, reads the time and finds a station |
| `systemadmin` | everything, the certificates included |

`viewer` and `systemadmin` are the node's, the other two are in
`VehicleAccess.cs`. The certificates are the one resource nobody but the
administrators may edit, which includes choosing the ones a session uses:
somebody who can add a root can make this vehicle believe a station nobody
else would. The configuration file may add roles and say differently what
one of them may do - see
[WWCP_Node's README](https://github.com/OpenChargingCloud/WWCP_Node#who-may-sign-in).

The pages follow the vehicle on `/api/v1/events`, and that stream asks, before
every event it sends and at every heartbeat, whether whoever opened it would
still be let in. Once the session it was opened with has ended - signed out,
expired, or taken back with the account's others - the stream ends too, without
the event, and the browser's next try is answered with a 401. One opened with
an API key ends the same way once the key is revoked or has run out, and one
opened with Basic auth once its password has changed.


## Certificates, and where they live

Everything this vehicle believes, everything it presents and every server it
recognises is in one store — WWCP_Node's `CertificateStore`, a directory of
files with an `index.json` beside them — and is addressed by a short handle
rather than by a path.

Three groups, and they behave differently in every respect that matters. A
**root** is what this vehicle believes: `v2gRoot` for the station's chain,
`moRoot` for a contract's, `oemRoot` for a provisioning chain, and `tlsRoot`
for a server the vehicle connects to - a time server, or a name server over
TLS or HTTPS - beside the roots of the machine it runs on. Any number of each
may be switched on at once, all of them are believed, and none is ever chosen
for a session. Keeping them apart is the point — one bag of roots would let an
OEM root vouch for a contract, which is the difference between a vehicle that
checks who is charging it and one that checks that somebody signed something.

A **credential** is what this vehicle presents: the Vehicle certificate is who
it is, the contract certificate is who pays, the OEM provisioning certificate
is what it was born with, and the tariff certificate is what a station's signed
tariff is checked against. Exactly one of each is chosen, and that choice is a
session setting — `SessionConfiguration` carries the handle, never the file.

A **server certificate** - `tlsServer` - is neither. It is what a server the
vehicle connects to shows, kept so that the server can be held to it by its
fingerprint, and never with a private key, which would be that server's key in
the wrong place. The Certificates page shows these as a third group, what the
vehicle *recognises*. The store keeps the node's two other TLS kinds as well,
`clientRoot` and `tlsIdentity`, which nothing in the vehicle uses yet. An
identity is told the listeners it is shown on where a kind of node names some;
a vehicle names none, so the page offers an identity nothing to be told.

A TLS root and a server certificate are told what they are for: the time
servers (`nts`), the name servers (`dns`), or - with nothing said - every use.
The Certificates page asks at the upload and again with **Uses**, because one
root may vouch for both, and a root kept for the name servers alone vouches for
no time. What a server is held to is said in its own entry, on the NTS and the
DNS page - see below - where its dialog offers the ones kept for it.

The store holds private keys **unencrypted**: a PKCS#12 is opened with its
password once, at import, and written back without one, so that any number of
certificates per role work without any number of passwords to carry. The file
system is what guards them, and the vehicle says so at every start and at every
import.


## Name resolution and the time

Both are the node's rather than the vehicle's: the `dns` and `nts` sections of
the configuration file, read and written the way every one of these programs
reads and writes them, so that a file written for a charging station or an
energy meter says the same to a vehicle. What their keys are, what each of
them is when the file says nothing, and how a group of time servers is asked,
agreed on and held to its certificates is written down once, in
[WWCP_Node's README](https://github.com/OpenChargingCloud/WWCP_Node#name-resolution-and-the-time).

The pages are the node's as well. The DNS client and NTS client pages,
WWCP_Node's like the frame they sit in, read and change the two sections
through `/api/v1/configuration/dns` and `/api/v1/configuration/nts`, every
node's routes, and ask a name server or a time server something from there;
asking a time server measures the clock and never steps it. The NTS page shows
the clock as well, and what counts as legal time - who stands behind it, and
how far off and how old a check may be - with a form of its own.

A time server, and a name server asked over TLS or HTTPS, can be held to a
certificate or a root, and the pages are where that is said: a server's dialog
takes SHA-256 fingerprints one to a line, adds the one the server showed last
or one the certificate store keeps for it with a click, and says what a
mismatch comes to and whether the server is held to what it is first believed
with. Its row says what was made of its certificate the last time - believed,
used although it did not match, or refused, and why - what it is held to, and
when it showed another certificate than before. A lookup on the DNS page says
the same of every certificate it met.

The whole list goes to the vehicle at every save, so every server goes with
what it is held to, and WWCP_Node's `ntsServers.ts`, `dnsServers.ts` and
`pins.ts` are where that is decided and tested: a list sent without the pins of
the servers nobody touched would let go of them, the ones learned on first use
included. And each server goes with what the page showed it held to, as
`pinsAsShown`, so that the vehicle changes only what was changed on the page: a
root a server learned while the page was open - the first key exchange or
handshake after a save is seconds later - stays when the page saves something
else. A name server switched to a transport that shows no certificate lets go
of its pins when it is saved - the vehicle would refuse them - and its row says
so first. Holding a server to a fingerprint is the service's, with the rest of
the server: a pin cannot make the vehicle believe a certificate that chains to
nothing this machine or its store holds, and what goes into the store stays the
administrators'.

`GET /api/v1/clock` serves the clock to anybody signed in, because a screen
that shows the time has to be able to say what it is worth: the time, the
group it is checked against, when it was last checked and how far off it was
then - and `legal`, with a `why` when it is not, `notClaimed` among them.


## What it is not

A car. Chains are validated only when trust roots say so and revocation never
is; the message timeout is a flat two seconds rather than the standard's
performance tables; and there is no electrical layer at all. The battery is
arithmetic on a simulated clock — linear below the taper knee, no temperature,
no losses, no ageing — so a run that ends "at 100 %" is reporting a sum and not
a charging curve.


## Your participation

This software is Open Source under the **Affero GPL 3.0 license**.
We appreciate your participation in this ongoing project, and your help to
improve it and the e-mobility ICT in general. If you find bugs, want to
request a feature or send us a pull request, feel free to use the normal
GitHub features to do so. For this please read the Contributor License
Agreement carefully and send us a signed copy or use a similar free and
open license.
