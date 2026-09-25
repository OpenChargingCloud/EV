import { config } from '../config';


// What the JSON API answers. Everything below /api/v1 except the sign-in needs
// the session cookie, which the browser sends by itself because every request
// here is same-origin.

/** How loudly a log entry asks to be read. */
export type LogLevel = 'debug' | 'info' | 'notice' | 'warning' | 'error' | 'critical';

/** The levels in the order the vehicle defines them, quietest first. */
export const logLevels: LogLevel[] = ['debug', 'info', 'notice', 'warning', 'error', 'critical'];

/** One thing that happened inside the vehicle. */
export interface LogEntry {
    /** A number that only ever grows, so the page can tell what it has seen. */
    id:         number;
    timestamp:  string;
    level:      LogLevel;
    /** What it is about: "ocpp", "15118", "http", ... - without the level. */
    tags:       string[];
    message:    string;
    /** Whatever else belongs to it, when there is more than one line to say. */
    data?:      unknown;
}

/** What a page of the log brings back. */
export interface LogPage {
    /** The newest id of the whole log, whatever this page was filtered by. */
    lastId:    number;
    capacity:  number;
    tags:      string[];
    entries:   LogEntry[];
}

/**
 * What somebody signed in to this vehicle may do.
 *
 * A copy of what the vehicle enforces, not the enforcement: it is here so a
 * page can grey out what this person may not do instead of offering it and
 * letting them find out by being refused. Every request is checked again on
 * arrival, so editing this list in a browser buys a button that answers 403.
 */
export type Permission = 'readConfiguration'
                       | 'changeNetworkSettings'
                       | 'runDiagnostics'
                       | 'changeChargingSettings'
                       | 'runSessions'
                       | 'manageCredentials';

/** Who is signed in to the web interface. */
export interface Me {
    username:     string;
    roles:        string[];
    permissions:  Permission[];
    session:      { createdAt: string; expiresAt: string };
}

/** How the vehicle is doing right now. */
export interface Status {
    service:    string;
    version:    string;
    hermod:     string | null;
    timestamp:  string;
    startedAt:  string;
    uptime:     string;
    sessions:   number;
    log:        { entries: number; capacity: number; lastId: number; tags: string[] };
}

/**
 * What the vehicle is. Only the shape the Configuration page relies on is
 * named; the rest is rendered from whatever the vehicle sends, so that a new
 * section on the server needs no change here.
 */
export interface Configuration {
    vehicle:     Record<string, unknown>;
    battery:     Record<string, unknown>;
    http:        Record<string, unknown>;
    web:         Record<string, unknown>;
    log:         Record<string, unknown>;
    time:        Record<string, unknown>;
    v2g:         Record<string, unknown>;
    assemblies:  Record<string, unknown>[];
}


/** One name server this vehicle asks. */
export interface DNSServer {
    /** An IP address or a host name. */
    address:              string;
    port:                 number;
    transport:            string;
    queryTimeoutSeconds:  number | null;
}

/** What may be changed about the name resolution while the vehicle runs. */
export interface DNSSettings {
    queryTimeoutSeconds:  number;
    /** null leaves it to the server's own default. */
    recursionDesired:     boolean | null;
    useCache:             boolean;
    dnssecOK:             boolean;
    followCNAMEs:         boolean;
    maxCNAMEFollows:      number;
    maxRetries:           number;
}

/** How this vehicle resolves names. */
export interface DNSConfiguration {
    enabled:    boolean;
    servers:    DNSServer[];
    settings:   DNSSettings;
    /** What was decided when the client was made, and is not on offer. */
    fixed:      Record<string, unknown>;
    limits: {
        maxServers:       number;
        maxQueryTimeout:  number;
        transports:       string[];
        recordTypes:      string[];
    };
    file:       string;
}

/** What a PUT to the DNS configuration may carry; everything is optional. */
export interface DNSUpdate {
    enabled?:              boolean;
    servers?:              DNSServer[];
    queryTimeoutSeconds?:  number;
    recursionDesired?:     boolean | null;
    useCache?:             boolean;
    dnssecOK?:             boolean;
    followCNAMEs?:         boolean;
    maxCNAMEFollows?:      number;
    maxRetries?:           number;
}

/** One resource record a test query brought back. */
export interface DNSRecord {
    name:        string;
    type:        string;
    timeToLive:  number;
    value:       string;
}

/** What a test query brought back. */
export interface DNSQueryResult {
    name:           string;
    /** Which single name server was asked, or null when all of them were. */
    asked?:         string | null;
    /** Set when an address was typed and a reverse name was asked for instead. */
    turnedAround?:  string | null;
    recordTypes:    string[];
    ok:             boolean;
    error?:         string;
    responseCode?:  string;
    server?:        string;
    runtime_ms?:    number;
    authoritative?: boolean;
    truncated?:     boolean;
    dnssec?:        string | null;
    timedOut?:      boolean;
    answers:        DNSRecord[];
    more?:          number;
}


/** One line of what happened while a time server was being asked. */
export interface TimeServerTestStep {
    at_ms:  number;
    level:  'info' | 'notice' | 'warning' | 'error';
    text:   string;
}

/** What came of asking one time server everything. */
export interface TimeServerTest {
    host:        string;
    ok:          boolean;
    runtime_ms:  number;
    steps:       TimeServerTestStep[];
}

/**
 * What may be changed about the time servers while the vehicle runs. What is
 * left out stays as it is; the list of servers is one value and replaces the
 * vehicle's whole.
 */
export interface NTSUpdate {
    enabled?:              boolean;
    servers?:              NTSServerEntry[];
    minServers?:           number;
    maxDeviationSeconds?:  number;
    checkEverySeconds?:    number;
    timeoutSeconds?:       number;
}

/**
 * One time server as the configuration names it. Whatever is left out is the
 * usual: priority 0, the usual ports, switched on.
 */
export interface NTSServerEntry {
    hostname:    string;
    priority?:   number;
    ntsKEPort?:  number;
    ntpPort?:    number;
    enabled?:    boolean;
}

/** How one synchronisation went, step by step. */
export interface NTSSyncResult {
    ok:           boolean;
    server:       string;
    at:           string;
    error?:       string;
    step?:        string;
    runtime_ms?:  number;
    offset_ms?:   number | null;

    /** What the group concluded: the median, how many answered, how far apart. */
    group?:       {
        name:               string;
        answered:           number;
        required:           number;
        offset_ms:          number | null;
        spread_ms:          number | null;
        deviationExceeded:  boolean;
    };

    /** One entry per server asked, answered or not. */
    servers?:     NTSServerResult[];

    /** Only from the detailed test of a single server. */
    ntske?:       Record<string, unknown>;
    ntp?:         Record<string, unknown>;
}

/** What one time server of a group said. */
export interface NTSServerResult {
    hostname:       string;
    ok:             boolean;
    offset_ms?:     number | null;
    roundTrip_ms?:  number | null;
    authenticated?: boolean | null;
    keyExchange?:   string;
    error?:         string | null;
}

/** One server of this vehicle's group, and what its key exchange is doing. */
export interface NTSTimeSource {
    hostname:       string;
    priority:       number;
    ntsKEPort:      number;
    ntpPort:        number;
    enabled:        boolean;
    cookies?:       number | null;
    lastExchange?:  string | null;
    aeadAlgorithm?: string | null;

    /**
     * The root CA the certificate chain of the last key exchange ended at -
     * the chain this vehicle built, so the root it judged the certificate by -
     * or null before the first exchange.
     */
    rootCA?:        NTSRootCA | null;
}

/** A root CA, by a name to call it, its subject, and its SHA-256 fingerprint. */
export interface NTSRootCA {
    name:         string;
    subject:      string;
    fingerprint:  string;
}

/** Where this vehicle gets the time from, and how its key exchange is doing. */
export interface NTSConfiguration {
    enabled:   boolean;

    /**
     * Every server this vehicle has, switched on or not, in the order they
     * were configured - and the rules for believing them.
     */
    timeSources?:  NTSTimeSource[];
    group?:        { name: string; minServers: number; maxDeviationSeconds: number };

    /**
     * What may be changed about the group and the test. The quorum is the one
     * wanted; the group's own can be lower while it has fewer servers on.
     */
    settings:  {
        timeoutSeconds:       number | null;
        checkEverySeconds:    number;
        minServers:           number;
        maxDeviationSeconds:  number;
    };
    /** What any new client starts with, the group's and the test's alike. */
    policy:    Record<string, unknown>;
    lastSync:  NTSSyncResult | null;
    limits:    {
        maxTimeout:        number;
        minCheckEvery:     number;
        maxCheckEvery:     number;
        minDeviation:      number;
        maxDeviation:      number;
        defaultNTSKEPort:  number;
        defaultNTPPort:    number;
    };
    file:      string;
    /** Only on the answer to a synchronisation, which carries both. */
    result?:   NTSSyncResult;
}


/** What this vehicle is, and what its battery wants. */
export interface VehicleConfiguration {
    name:     string;
    vin:      string | null;
    battery: {
        capacityKWh:                 number;
        stateOfChargePercent:        number;
        targetStateOfChargePercent:  number;
        maxChargingPowerKW:          number;
        taperFromPercent:            number;
    };
    limits: {
        maxCapacityKWh:   number;
        maxPowerKW:       number;
        maxNameLength:    number;
    };
    file:     string;
}

/** What a save sends. Only the fields given are changed. */
export interface VehicleUpdate {
    name?:                        string;
    vin?:                         string;
    batteryCapacityKWh?:          number;
    stateOfChargePercent?:        number;
    targetStateOfChargePercent?:  number;
    maxChargingPowerKW?:          number;
    taperFromPercent?:            number;
}


/** One interface of this machine that could carry V2G traffic. */
export interface V2GInterface {
    name:       string;
    index:      number;
    linkLocal:  string;
    mac:        string;
}

/** One vehicle that answered an SDP request. */
export interface SECC {
    /** The address in the payload: where the vehicle would connect. */
    address:    string;
    port:       number;
    security:   'tls' | 'noTls';
    transport:  string;
    version:    string;
    /** The address on the packet: who actually sent it. Only for the first answer. */
    from:       string | null;
    /** Why this answer was refused, when it was. */
    reason?:    string;
}

/**
 * How one discovery went.
 *
 * "found" and "rejected" both mean something answered; the difference is
 * whether the answer was usable. "timeout" means nothing answered at all,
 * which against a link with no vehicle on it is the expected outcome and not
 * an error.
 */
export interface DiscoveryResult {
    outcome:      'found' | 'rejected' | 'timeout' | 'cancelled' | 'failed' | 'busy' | 'noInterface';
    startedAt?:   string;
    attempts?:    number;
    elapsed_ms?:  number;
    interface?:   string;
    secc?:        SECC;
    others?:      SECC[];
    rejected?:    SECC[];
    error?:       string;
}

/** The wire below the charging cable, from this vehicle's side. */
export interface V2GConfiguration {
    /** The configured interface, or null for "the first candidate". */
    interface:   string | null;
    interfaces:  V2GInterface[];
    settings: {
        requestedSecurity:            'tls' | 'noTls';
        perAttemptTimeoutSeconds:     number;
        maxRetries:                   number;
        totalDeadlineSeconds:         number;
        rejectNoTLSResponses:         boolean;
        requireLinkLocalSECCAddress:  boolean;
        multicastLoopback:            boolean;
    };
    lastDiscovery:  DiscoveryResult | null;
    file:           string;
    /** Only on the answer to a discovery. */
    result?:        DiscoveryResult;
}

/**
 * What a save sends. Only the fields given are changed - with one exception:
 * an explicit null for "interface" takes a named interface back, where leaving
 * it out would keep it. There is no other way of saying "whichever one comes
 * first" once one has been chosen.
 */
export interface V2GUpdate {
    interface?:                    string | null;
    requestedSecurity?:            'tls' | 'noTls';
    perAttemptTimeoutSeconds?:     number;
    maxRetries?:                   number;
    totalDeadlineSeconds?:         number;
    rejectNoTLSResponses?:         boolean;
    requireLinkLocalSECCAddress?:  boolean;
    multicastLoopback?:            boolean;
}



/** One SLAC pairing: which network the vehicle and the station agreed on. */
export interface SlacResult {
    outcome:      'paired' | 'cancelled' | 'failed' | 'notConfigured' | 'busy';
    startedAt?:   string;
    elapsed_ms?:  number;
    peer?:        string;
    /** The network identifier. The key it agreed on is deliberately not here. */
    nid?:         string;
    error?:       string;
}

/** One attachment to the coupler's 10BASE-T1S bus: which medium, and as which node. */
export interface T1SResult {
    outcome:      'attached' | 'notAttached' | 'declined' | 'noMedium' | 'cancelled' | 'failed' | 'notConfigured' | 'busy';
    startedAt?:   string;
    elapsed_ms?:  number;
    /** Which kind of medium was decided on: none, auto, afpacket or udp. */
    transport?:   string;
    /** What the medium is, for a reader: "UDP multicast 239.151.18.1:2354" or "AF_PACKET on eth1". */
    medium?:      string;
    mac?:         string;
    coordinator?: string;
    nodeId?:      number;
    weight?:      number;
    /** Why there was no bus to join and nothing wrong with that. */
    reason?:      string;
    error?:       string;
}

/** What the pack did over one session. */
export interface SessionBattery {
    capacityKWh:           number;
    startedAtPercent:      number;
    stateOfChargePercent:  number;
    deliveredKWh:          number;
    /** One iteration is one simulated minute. */
    iterations:            number;
    simulatedMinutes:      number;
    /** Which goal ended it. */
    stoppedBecause:        string | null;
    /** Whether the floor the driver named was missed. Reported, never a stop condition. */
    minimumMissed:         boolean;
    describe:              string | null;
}

/**
 * How one session went.
 *
 * "completed" means it ran to SessionStop, which is not the same as it having
 * charged: a station that refused authorization completes too, and says so in
 * the fields rather than in the outcome.
 */
export interface SessionRun {
    outcome:             'completed' | 'failed' | 'cancelled' | 'busy' | 'slacFailed' | 't1sFailed' | 'noStation';
    error?:              string;
    startedAt?:          string;
    elapsed_ms?:         number;
    station?:            string;
    protocol?:           string;
    mode?:               string;
    sessionId?:          string;
    sessionSetup?:       string;
    exchanges?:          number;
    bytesOnWire?:        number;
    authorization?:      string;
    meteringReceipts?:   number;
    renegotiations?:     number;
    paused?:             boolean;
    pausedSessionId?:    string;
    resumeRefused?:      boolean;
    /** -20 only: whether a rejoined session was proved to be with the same station. */
    sameStation?:        boolean | null;
    contractInstalled?:  boolean;
    battery?:            SessionBattery | null;
    tariff?:             { signaturePresent: boolean; digestOk: boolean; signatureOk: boolean; tuplesOffered?: number } | null;
    /** The stages that ran before the session, where they did. */
    slac?:               SlacResult;
    t1s?:                T1SResult;
    discovery?:          DiscoveryResult;
    /** The first half, on a run that paused and rejoined. */
    pausedRun?:          SessionRun;
}

/**
 * What a certificate is for. Roots are believed and the vehicle's own
 * certificates are presented; a server certificate is neither, but kept to
 * recognise a server by its fingerprint.
 */
export type CertificateKind = 'v2gRoot' | 'moRoot' | 'oemRoot'
                            | 'vehicle' | 'contract' | 'oemProvisioning' | 'tariffVerification'
                            | 'tlsRoot' | 'clientRoot' | 'tlsServer' | 'tlsIdentity';

/** One certificate in the store. Everything but label and active is read out of the file. */
export interface Certificate {
    /** The handle it is addressed by: the first 16 digits of its fingerprint. */
    id:             string;
    kind:           CertificateKind;
    fileName:       string;
    label:          string;
    subject:        string;
    issuer:         string;
    serialNumber:   string;
    /** Its SHA-256 fingerprint in full, for comparing against what a CA said. */
    thumbprint:     string;
    notBefore:      string;
    notAfter:       string;
    keyAlgorithm:   string;
    hasPrivateKey:  boolean;
    /** How many further certificates travel with it, e.g. its sub-CAs. */
    chainLength:    number;
    /** Whether this vehicle is using it. Somebody switches this; time does not. */
    active:         boolean;
    importedAt:     string;
    expired:        boolean;
    notYetValid:    boolean;
    /** Active, and inside its own validity. */
    usable:         boolean;
    description:    string;
}

/** What one of the session's certificate slots is set to, resolved against the store. */
export interface ChosenCertificate {
    id:         string;
    /** True when the store no longer has it - a different problem from nothing chosen. */
    missing:    boolean;
    label?:     string;
    subject?:   string;
    notAfter?:  string;
    usable?:    boolean;
}

/** The whole store, grouped the way it is shown. */
export interface CertificateStore {
    directory:     string;
    /** The kinds that are trust anchors, in the order they are shown. */
    trustAnchors:  CertificateKind[];
    /** The kinds that are presented, in the order they are shown. */
    credentials:   CertificateKind[];
    kinds:         Record<CertificateKind, {
                       description:     string;
                       trustAnchor:     boolean;
                       needsPrivateKey: boolean;
                   }>;
    certificates:  Record<CertificateKind, Certificate[]>;
    /** Which handle each session slot currently names. */
    chosen: {
        vehicleCertificate:   string | null;
        contractCertificate:  string | null;
        oemCertificate:       string | null;
        tariffCertificate:    string | null;
    };
    /** Whether anything in the store carries a private key, which is kept unencrypted. */
    keysAreUnencrypted: boolean;
}

/** What an import sends: the file, base64-encoded, and what to make of it. */
export interface CertificateImport {
    kind:       CertificateKind;
    /** The file's bytes, base64-encoded. PEM, DER or PKCS#12. */
    content:    string;
    /** What opens it, where it is a protected PKCS#12. Used once and not kept. */
    password?:  string;
    /** What to call it; its common name where this is left out. */
    label?:     string;
}

/** What a change to a stored certificate may say. Everything else is read from the file. */
export interface CertificateUpdate {
    active?:  boolean;
    label?:   string | null;
}

/** What this vehicle does once it has found a station. */
export interface SessionConfiguration {
    /** The station to drive to, or null to look for one. */
    connect:   string | null;
    settings: {
        protocol:     '2' | '20' | 'both';
        mode:         'ac' | 'dc' | 'mcs';
        tls:          'none' | 'dotnet' | 'bc';
        renegotiate:  boolean;
        slacPeer:     string | null;
        /** The 10BASE-T1S bus of an MCS coupler: which medium, where, and how often to be asked. */
        t1sTransport: 'none' | 'auto' | 'afpacket' | 'udp';
        t1sBus:       string | null;
        t1sInterface: string | null;
        t1sWeight:    number;
    };
    certificates: {
        pkiDirectory:         string | null;
        /** What each slot is currently set to, resolved against the store. */
        vehicleCertificate:   ChosenCertificate | null;
        contractCertificate:  ChosenCertificate | null;
        oemCertificate:       ChosenCertificate | null;
        tariffCertificate:    ChosenCertificate | null;
        /** How many usable roots of each kind this vehicle believes. */
        trustAnchors:         { v2gRoot: number; moRoot: number; oemRoot: number };
    };
    goals: {
        targetEnergyKWh:              number | null;
        maxChargingTimeSeconds:       number | null;
        departureInSeconds:           number | null;
        minimumStateOfChargePercent:  number | null;
    };
    running:      boolean;
    /** A paused session waiting to be rejoined, by its identification. */
    paused:       string | null;
    lastSession:  SessionRun | null;
    file:         string;
}

/**
 * What a save sends. Only the fields given are changed - with the same
 * exception as the ISO 15118 page: an explicit null takes a setting back,
 * where leaving it out keeps it.
 */
export interface SessionUpdate {
    connect?:                      string | null;
    protocol?:                     '2' | '20' | 'both' | null;
    mode?:                         'ac' | 'dc' | 'mcs' | null;
    tls?:                          'none' | 'dotnet' | 'bc' | null;
    renegotiate?:                  boolean | null;
    slacPeer?:                     string | null;
    t1sTransport?:                 'none' | 'auto' | 'afpacket' | 'udp' | null;
    t1sBus?:                       string | null;
    t1sInterface?:                 string | null;
    t1sWeight?:                    number | null;
    pkiDirectory?:                 string | null;
    vehicleCertificate?:           string | null;
    contractCertificate?:          string | null;
    oemCertificate?:               string | null;
    tariffCertificate?:            string | null;
    targetEnergyKWh?:              number | null;
    maxChargingTimeSeconds?:       number | null;
    departureInSeconds?:           number | null;
    minimumStateOfChargePercent?:  number | null;
}

/** What one run is asked to do, beyond what the settings already say. */
export interface SessionStart {
    /** A station for this run only; without one, the configured one or SDP. */
    connect?:      string;
    /** End paused rather than terminated, so that it can be rejoined. */
    pause?:        boolean;
    /** Rejoin this paused session, by its identification in hexadecimal. */
    resume?:       string;
    /** Both halves in one run: charge, pause, reconnect, rejoin. */
    pauseResume?:  boolean;
}


/**
 * The vehicle answered, and said no.
 *
 * The fields are written out rather than declared in the constructor, as are
 * NoAnswer's below: constructor parameter properties are one of the few pieces
 * of TypeScript that cannot simply be stripped away, and this file is read as
 * it stands by the same test runner that reads the display's rules.
 */
export class ApiError extends Error {

    readonly status:  number;
    readonly body?:   unknown;

    constructor(status:   number,
                message:  string,
                body?:    unknown) {

        super(message);

        this.name    = 'ApiError';
        this.status  = status;
        this.body    = body;

    }

    get isUnauthorized(): boolean {
        return this.status === 401;
    }

}


/**
 * Nothing came back at all.
 *
 * Not an ApiError, because the two are different things to be told: an
 * ApiError is the vehicle answering and saying no, with a sentence of its own
 * about why. This is the vehicle saying nothing - and a page that can tell the
 * two apart can say so, instead of repeating a status that was never sent.
 */
export class NoAnswer extends Error {

    readonly reason:  'ran out of time' | 'could not be reached';

    constructor(reason:   'ran out of time' | 'could not be reached',
                message:  string) {

        super(message);

        this.name    = 'NoAnswer';
        this.reason  = reason;

    }

}


/**
 * How long the web interface waits for the vehicle to answer about itself.
 *
 * Measured against a vehicle that had gone quiet rather than away - the case
 * a refused connection does not cover, and the one a car park's network
 * actually produces: 98 seconds after Save, the request was still open, both
 * buttons of the form were still greyed out, and the page said nothing at all.
 * Seven pages clicked through in that state left nine requests hanging, more
 * than the browser will even keep connections open for.
 *
 * Fifteen seconds is four orders of magnitude more than this vehicle needs:
 * every read and write of its own configuration measured between 1 and 7
 * milliseconds. That is the point. The deadline is here to notice silence and
 * not slowness, so it can be generous enough that a slow link never trips it.
 */
export const answerWithin = 15_000;

/**
 * And how long for the vehicle to do something and then answer.
 *
 * Longer, because a write is a file and - for the EVSEs - the OCPP nodes being
 * rebuilt from it, and because giving up on a write is the worse mistake of
 * the two to make: the vehicle may have carried it out and only been slow to
 * say so.
 */
export const actWithin = 30_000;

/**
 * How long a question the vehicle has to put to somebody else may take: the
 * timeouts of the steps it takes one after another, added up, and the usual
 * allowance on top - so that what the page gives up on is silence from the
 * vehicle rather than patience it was told to have.
 */
export function afterAsking(Timeouts: number[]): number {
    return Timeouts.reduce((total, seconds) => total + seconds * 1000, 0) + answerWithin;
}


let unauthorizedHandler: (() => void) | null = null;

/** Called whenever the API answers 401, i.e. the session is gone. */
export function onUnauthorized(handler: () => void): void {
    unauthorizedHandler = handler;
}


/**
 * One request to the vehicle, with a deadline.
 *
 * The deadline covers reading the body as well as opening the connection: a
 * vehicle that sends its headers and then stops mid-answer hangs exactly as
 * thoroughly as one that never starts.
 *
 * Exported so that the tests can drive it at a deadline short enough to be a
 * test; everything the pages do goes through `api` below.
 */
/**
 * Sign in at the HTTPExt API and answer with who is now signed in.
 *
 * Two requests rather than one, and that is not a detour. The HTTPExt API is
 * the only place that can check a password - the store it reads is private to
 * it - but it answers in its own shape and knows nothing of this vehicle's
 * roles. So it sets the session cookie, and "me" is asked afterwards for the
 * roles and permissions this frontend actually works from.
 *
 * Form-urlencoded because that is what its sign-in route accepts, and the
 * field is called "login" rather than "username".
 */
async function signIn(username: string, password: string): Promise<Me> {

    const giveUp = new AbortController();
    const timer  = setTimeout(() => giveUp.abort(), actWithin);

    let response: Response;

    try
    {
        response = await fetch(config.extBase + '/login', {
                             method:       'POST',
                             headers:      {
                                               'Content-Type':  'application/x-www-form-urlencoded',
                                               'Accept':        'application/json'
                                           },
                             credentials:  'same-origin',
                             signal:       giveUp.signal,
                             body:         new URLSearchParams({ login: username, password }).toString()
                         });
    }
    catch (problem)
    {
        throw nothingCameBack(problem, 'POST', actWithin, giveUp.signal.aborted);
    }
    finally
    {
        clearTimeout(timer);
    }

    if (!response.ok) {

        // Its refusals carry a "description"; ours carry an "error". Both are
        // shown to somebody who just typed a password, so both are read.
        let message = `${response.status} ${response.statusText}`;

        try {
            const json = JSON.parse(await response.text());
            if (typeof json === 'object' && json !== null) {
                if      ('description' in json && typeof json.description === 'string')  message = json.description;
                else if ('error'       in json && typeof json.error       === 'string')  message = json.error;
            }
        }
        catch { /* the status line says enough */ }

        throw new ApiError(response.status, message, null);

    }

    return request<Me>('GET', '/auth/me');

}


export async function request<T>(method:  string,
                                 path:    string,
                                 body?:   unknown,
                                 within:  number = method === 'GET' ? answerWithin : actWithin): Promise<T> {

    const headers: Record<string, string> = { 'Accept': 'application/json' };

    if (body !== undefined)
        headers['Content-Type'] = 'application/json';

    const giveUp = new AbortController();
    const timer  = setTimeout(() => giveUp.abort(), within);

    let response:  Response;
    let text:      string;

    try
    {

        // Same origin, so the session cookie travels with every request.
        response = await fetch(config.apiBase + path, {
                             method,
                             headers,
                             credentials: 'same-origin',
                             signal:      giveUp.signal,
                             body:        body !== undefined ? JSON.stringify(body) : undefined
                         });

        if (response.status === 401)
            unauthorizedHandler?.();

        if (response.status === 204) {
            // Nothing to read, but reading it lets the browser finish the
            // request cleanly instead of aborting an unconsumed body.
            await response.arrayBuffer();
            return undefined as T;
        }

        text = await response.text();

    }
    catch (problem)
    {
        throw nothingCameBack(problem, method, within, giveUp.signal.aborted);
    }
    finally
    {
        clearTimeout(timer);
    }

    let json: unknown = null;

    try {
        json = text.length > 0 ? JSON.parse(text) : null;
    }
    catch {
        if (response.ok)
            throw new ApiError(response.status, `Invalid JSON in the response of ${method} ${path}`, text);
    }

    if (!response.ok) {

        const message = typeof json === 'object' && json !== null && 'error' in json && typeof json.error === 'string'
                            ? json.error
                            : `${response.status} ${response.statusText}`;

        throw new ApiError(response.status, message, json);

    }

    return json as T;

}


/**
 * What to say when nothing came back, in words somebody can act on.
 *
 * A read that runs out of time changed nothing, and can be told so. A write
 * that runs out of time is the honest awkward case: the page stopped waiting,
 * but the vehicle may well have done the thing and been slow to say so, and
 * telling somebody that it did not work would invite them to do it twice. So
 * it says what is actually known - that the waiting stopped - and where to
 * look for the rest.
 */
function nothingCameBack(Problem:  unknown,
                         Method:   string,
                         Within:   number,
                         GaveUp:   boolean): unknown {

    const seconds = Math.round(Within / 1000);

    if (GaveUp)
        return new NoAnswer(
                   'ran out of time',
                   Method === 'GET'
                       ? `The vehicle did not answer within ${seconds} seconds. ` +
                         'It may be busy, restarting, or no longer reachable from here.'
                       : `The vehicle did not answer within ${seconds} seconds, so this page ` +
                         'stopped waiting. It may still have carried this out - reload to see ' +
                         'what it now says.'
               );

    // The browser's own word for this is "Failed to fetch", which on a page
    // about a vehicle names neither the vehicle nor what to do next.
    if (Problem instanceof TypeError)
        return new NoAnswer(
                   'could not be reached',
                   'The vehicle could not be reached. It may be switched off, restarting, ' +
                   'or on the other side of a network that is down.'
               );

    return Problem;

}


export const api = {

    /** The Server-Sent Events stream; the browser sends the session cookie along. */
    eventsURL: `${config.apiBase}/events`,

    auth: {
        me:      ()                                    => request<Me>  ('GET',  '/auth/me'),
        login:   signIn,
        logout:  ()                                    => request<void>('POST', '/auth/logout')
    },

    status:         () => request<Status>       ('GET', '/status'),
    configuration:  () => request<Configuration>('GET', '/configuration'),

    dns: {
        get:   ()                    => request<DNSConfiguration>('GET', '/configuration/dns'),
        /** Only the fields given are changed; the answer is the whole configuration as it now stands. */
        save:  (update: DNSUpdate)   => request<DNSConfiguration>('PUT', '/configuration/dns', update),
        /**
         * Make the vehicle look a name up. A POST because it sends traffic.
         *
         * @param seconds  how long the name servers asked may take - see
         *                 pages/dnsServers.ts.
         * @param server   which configured name server to ask, by its place in
         *                 the list - or undefined to resolve the way the
         *                 vehicle resolves anything else, asking all of them
         *                 at once.
         */
        query: (name: string, recordTypes: string[], seconds: number, server?: number) =>
                   request<DNSQueryResult>('POST', '/configuration/dns/query', { name, recordTypes, server },
                                           afterAsking([ seconds ]))
    },

    nts: {
        get:   ()                    => request<NTSConfiguration>('GET', '/configuration/nts'),
        save:  (update: NTSUpdate)   => request<NTSConfiguration>('PUT', '/configuration/nts', update),
        /**
         * Ask one time server everything: the name, the key exchange, the
         * authenticated NTP request, each one written down as it happens.
         *
         * @param timeoutSeconds  what the vehicle allows each of the two steps.
         * @param host            which server, on the ports it is configured
         *                        with, or undefined for the configured one.
         */
        test:  (timeoutSeconds: number, host?: string) => request<TimeServerTest>(
                                               'POST', '/configuration/nts/test', { host },
                                               afterAsking([timeoutSeconds, timeoutSeconds])),
        /**
         * Ask every server of the group, with every step in the log - two steps
         * over the network per server, so two of the vehicle's own timeouts
         * before the page stops believing in it.
         *
         * @param timeoutSeconds  what the vehicle allows each of the two steps.
         */
        sync:  (timeoutSeconds: number) => request<NTSConfiguration>(
                                               'POST', '/configuration/nts/sync', {},
                                               afterAsking([timeoutSeconds, timeoutSeconds])
                                           )
    },

    vehicle: {
        get:   ()                        => request<VehicleConfiguration>('GET', '/configuration/vehicle'),
        /** Only the fields given are changed; the answer is the whole configuration as it now stands. */
        save:  (update: VehicleUpdate)   => request<VehicleConfiguration>('PUT', '/configuration/vehicle', update)
    },

    v2g: {

        get:   ()                    => request<V2GConfiguration>('GET', '/configuration/v2g'),
        save:  (update: V2GUpdate)   => request<V2GConfiguration>('PUT', '/configuration/v2g', update),

        /**
         * Multicast an SDP request onto the link and say what answered.
         *
         * A POST because it puts packets on a link every machine on that link
         * receives, which is not something to leave in a URL a browser may
         * repeat.
         *
         * @param deadlineSeconds  what the vehicle allows the whole discovery.
         *                         The page waits for that and a little more,
         *                         because the answer still has to come back.
         * @param interfaceName    which interface to broadcast on, or undefined
         *                         for the configured one.
         */
        discover: (deadlineSeconds: number, interfaceName?: string) =>
                      request<V2GConfiguration>('POST', '/configuration/v2g/discover',
                                                { interface: interfaceName },
                                                afterAsking([ deadlineSeconds ])),

        /**
         * Run the SLAC pairing stage on its own, against the configured peer.
         *
         * Seconds rather than minutes - the collection windows are 600 ms and
         * 1200 ms - so unlike a session this one is waited for.
         */
        pair: () => request<SlacResult>('POST', '/configuration/v2g/pair', {}, afterAsking([ 10 ]))

    },

    certificates: {

        /** The whole store, grouped by kind. */
        get:     ()                                       => request<CertificateStore>('GET', '/certificates'),

        /**
         * Put a certificate into the store.
         *
         * Importing the same file twice is the same entry - the handle is its
         * fingerprint - so this is safe to repeat.
         */
        import:  (certificate: CertificateImport)         => request<Certificate>('POST', '/certificates', certificate),

        /** Switch one on or off, or rename it. */
        update:  (id: string, update: CertificateUpdate)  => request<Certificate>('PATCH', `/certificates/${encodeURIComponent(id)}`, update),

        /** Take one out of the store and delete its file. Refused while a session names it. */
        remove:  (id: string)                             => request<CertificateStore>('DELETE', `/certificates/${encodeURIComponent(id)}`),

        /**
         * Read the store directory again.
         *
         * For certificates somebody copied in rather than uploaded - which is a
         * perfectly good way to install one on a machine you already have a
         * shell on.
         */
        reload:  ()                                       => request<CertificateStore>('POST', '/certificates/reload', {})

    },

    session: {

        get:   ()                        => request<SessionConfiguration>('GET', '/session'),
        save:  (update: SessionUpdate)   => request<SessionConfiguration>('PUT', '/configuration/session', update),

        /**
         * Start a session.
         *
         * This answers before the session is over, and on purpose: a full
         * charge is hundreds of exchanges and minutes of wall clock, which is
         * longer than a browser will hold a request open. So the vehicle says
         * it has started and the exchange itself arrives on the event stream -
         * watch the Logs page, and ask `get` for the sum when `running` goes
         * false.
         */
        start: (options: SessionStart)   => request<{ outcome: string }>('POST', '/session', options),

        /** End the session that is running. What was metered stays metered. */
        stop:  ()                        => request<{ outcome: string }>('POST', '/session/stop', {})

    },

    /**
     * A page of the log, oldest of the returned entries first.
     *
     * @param limit  at most this many entries
     * @param after  only what is newer than this id
     * @param tag    only entries carrying this tag - a level counting as one
     */
    logs: (limit?: number, after?: number, tag?: string) => {

        const query = new URLSearchParams();

        if (limit !== undefined)  query.set('limit', String(limit));
        if (after !== undefined)  query.set('after', String(after));
        if (tag)                  query.set('tag',   tag);

        const suffix = query.size > 0 ? `?${query}` : '';

        return request<LogPage>('GET', `/logs${suffix}`);

    }

};
