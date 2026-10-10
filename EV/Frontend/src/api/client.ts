import { afterAsking,
         nodeAPI,
         request,
         type Certificate        as NodeCertificate,
         type CertificateImport  as NodeCertificateImport,
         type CertificateStore   as NodeCertificateStore,
         type NodeConfiguration,
         type NodeMe,
         type NodeResource,
         type NodeStatus,
         type Operation }  from '@node/api/client';


// What every node answers - the log, name resolution, the time, the store,
// who is signed in - and how it is asked are WWCP_Node's, and every page here
// reads them from this module as before. What follows is what a vehicle adds:
// its resources, what its configuration says beyond every node's, the kinds
// its store keeps and the certificates a session is given, and its own
// routes - the vehicle, the V2G link and the session.
export * from '@node/api/client';


/**
 * What a role may be allowed to touch on this vehicle: what every node has,
 * and what a vehicle adds to it.
 */
export type Resource = NodeResource | 'vehicle' | 'v2g' | 'session';

/** What somebody signed in to this vehicle may do: an operation on a resource, written "dns:edit". */
export type Permission = `${Resource}:${Operation}`;

/** Who is signed in to the web interface. */
export type Me = NodeMe<Resource>;

/** How the vehicle is doing right now: what every node says of itself, and nothing a vehicle adds. */
export type Status = NodeStatus;

/**
 * What the vehicle is: every node's sections, and its own. Only the shape the
 * Configuration page relies on is named; the fields of each section are
 * rendered from whatever the vehicle sends, so that a field added on the
 * server needs no change here.
 */
export interface Configuration extends NodeConfiguration {
    vehicle:  Record<string, unknown>;
    battery:  Record<string, unknown>;
    v2g:      Record<string, unknown>;
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

/** One station that answered an SDP request. */
export interface SECC {
    /** The address in the payload: where the vehicle would connect. */
    address:    string;
    port:       number;
    security:   'tls' | 'noTls';
    transport:  string;
    version:    string;
    /** The address on the packet: who actually sent it, for every answer. */
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
    /** How this vehicle is plugged in, or null where it is not. */
    link:           Link | null;
    file:           string;
    /** Only on the answer to a discovery. */
    result?:        DiscoveryResult;
}

/** How a vehicle is plugged in: straight onto the link, after a SLAC pairing, or onto a coupler's bus. */
export type LinkMedium = 'direct' | 'slac' | 't1s';

/** A link this vehicle holds until it is unplugged, and what plugging in said. */
export interface Link {
    outcome:  'pluggedIn';
    via:      LinkMedium;
    since:    string;
    slac?:    SlacResult;
    t1s?:     T1SResult;
}

/**
 * Plugging in: how, and - for this plugging in only - the SLAC peer and the
 * bus where they differ from the session settings.
 */
export interface LinkRequest {
    via:            LinkMedium;
    slacPeer?:      string;
    t1sTransport?:  'auto' | 'afpacket' | 'udp';
    t1sBus?:        string;
    t1sInterface?:  string;
    t1sWeight?:     number;
}

/** What came of plugging in: the link, or why there is none. */
export type PlugInResult = Link | {
    outcome:  'notConfigured' | 'busy' | 'failed' | 'cancelled' | 'notAttached' | 'declined' | 'noMedium';
    error:    string;
    slac?:    SlacResult;
    t1s?:     T1SResult;
};

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
    /** The station at the end of the cable: the one that heard the vehicle loudest. */
    station?:     SlacStation;
    /** Every station that answered the sounding. */
    candidates?:  SlacStation[];
    error?:       string;
}

/** One station that answered a SLAC sounding, and how loudly it heard the vehicle. */
export interface SlacStation {
    mac:              string;
    /** The average attenuation, in dB: the lower, the louder. */
    attenuation_dB?:  number;
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
    /** The station told the vehicle to end the charging - Terminate over -20, StopCharging over -2 - so it ended the session for good. */
    terminatedByStation?: boolean;
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

/** One certificate in this vehicle's store. */
export type Certificate = NodeCertificate<CertificateKind>;

/** What an import sends. */
export type CertificateImport = NodeCertificateImport<CertificateKind>;

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

/** One certificate a session slot names, and the kind it names it as. */
export interface ChosenAs {
    id:    string;
    kind:  CertificateKind;
}

/** The whole store, and which of it each session slot names. */
export interface CertificateStore extends NodeCertificateStore<CertificateKind> {
    /**
     * Which certificate each session slot currently names, as the kind the
     * slot wants: a certificate kept as several kinds is chosen as one of
     * them. It is not taken out of the store as that kind while it is.
     */
    chosen: {
        vehicleCertificate:   ChosenAs | null;
        contractCertificate:  ChosenAs | null;
        oemCertificate:       ChosenAs | null;
        tariffCertificate:    ChosenAs | null;
    };
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
    /** One of the stations the last discovery found, by its place in the answer: 0 the first. */
    station?:      number;
    /** How this run's connection is secured, instead of what the settings say. */
    tls?:          'none' | 'dotnet' | 'bc';
    /** End paused rather than terminated, so that it can be rejoined. */
    pause?:        boolean;
    /** Rejoin this paused session, by its identification in hexadecimal. */
    resume?:       string;
    /** Both halves in one run: charge, pause, reconnect, rejoin. */
    pauseResume?:  boolean;
}


/** The routes every node has, typed with what a vehicle says its own of them are. */
const node = nodeAPI<{ me: Me; status: Status; configuration: Configuration; kind: CertificateKind; store: CertificateStore }>();

export const api = {

    ...node,

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

    link: {

        /**
         * Plug in, and stay plugged in until unplugged: a session then runs
         * over this link without pairing or joining the bus again. Seconds, as
         * a pairing or joining a bus is, so waited for.
         */
        plugIn:  (how: LinkRequest) => request<Omit<V2GConfiguration, 'result'> & { result: PlugInResult }>('POST', '/link', how, afterAsking([ 15 ])),

        /** Unplug: leave the bus, forget the pairing. */
        unplug:  ()                 => request<V2GConfiguration>('DELETE', '/link')

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

    }

};
