namespace Ipc.Api;

public sealed record ApiContract(string Name, string Description, string Counts, string Formats, string Notes)
{
    public IReadOnlyList<string> Describe() => ["API QSYS/" + Name, Description, "Parameter counts: " + Counts,
        "Formats: " + Formats, Notes, "Binary integers use big-endian bytes.",
        "Character names and error IDs use the job CCSID.", "See docs/api.md for layouts and supported options."];
}

/// <summary>Contracts of callable adapters; the release inventory also contains APIs not yet executable.</summary>
public static class ApiContractCatalog
{
    public static IReadOnlyList<ApiContract> All { get; } = Array.AsReadOnly(new ApiContract[] {
        new("QCMDEXC", "Execute command", "2, 3", "Command bytes and packed DEC(15,5) length", "Uses shared command execution; interactive screen requests fail."),
        new("QSNDDTAQ", "Send data queue", "4, 6, 7, 8", "Packed DEC(5,0), optional key/sender/CCSID", "Local standard queues; no DDM or pointers."),
        new("QRCVDTAQ", "Receive data queue", "5, 10, 13, 14", "Packed DEC(5,0), writable data/key/sender", "Truncation, peek and cancelable waits use shared queue storage."),
        new("QCLRDTAQ", "Clear data queue", "2, 6", "Optional keyed comparison and error receiver", "Local standard queues; clearing honors queue authority."),
        new("QUSCRTUS", "Create user space", "6, 8, 9, 11, 12", "Qualified name, binary size, initial byte", "Replacement, domain and selected growth options are supported."),
        new("QUSRUSAT", "Retrieve user space attributes", "5", "SPCA0100", "Returned and available lengths describe a bounded receiver."),
        new("QUSCHGUS", "Change user space", "5, 6, 7", "Binary position/length and data bytes", "Writes and optional extension are atomic."),
        new("QUSRTVUS", "Retrieve user space", "4, 5", "Binary position/length and writable bytes", "Returns exact selected storage bytes."),
        new("QUSCUSAT", "Change user space attributes", "4", "Attribute entries with binary keys", "Selected size, transfer and growth attributes are supported."),
        new("QUSDLTUS", "Delete user space", "2", "Qualified name and error receiver", "Uses generic object deletion and current object authority."),
        new("QWCRDTAA", "Retrieve data area", "6", "Fixed 36-byte header and typed value", "Named and job data areas; no DDM retrieval."),
        new("QWCRSVAL", "Retrieve system values", "5", "Offset directory and C/B value tables", "27 supported values; returns complete entries fitting the receiver."),
        new("QUSRJOBI", "Retrieve job information", "5, 6, 7", "JOBI0100, JOBI0700", "Opaque job IDs; protected library lists; no performance reset."),
        new("QUSLJOB", "List jobs into user space", "4, 5, 8, 9", "JOBL0100, JOBL0200; generic header 0100", "17 keyed attributes; implicit growth, partial lists and continuation."),
        new("QWCCVTDT", "Convert date and time format", "5, 10, 11", "YMD/MDY/DMY/JUL; long forms; SYSVAL/CURRENT/DTS/DOS", "Millisecond/microsecond layouts; SYS/UTC. JOB/TIMZON metadata remains open."),
        new("QC3CALHA", "Calculate hash", "9", "DATA0100, ALGD0500", "MD5/SHA-1/SHA-256/SHA-384/SHA-512; software provider."),
        new("QC3CALHM", "Calculate HMAC", "11", "DATA0100, ALGD0500, KEYD0100/0200", "Five hash algorithms; opaque job-owned or binary keys."),
        new("QC3ENCDT", "Encrypt data", "13", "DATA0100, ALGD0200, KEYD0100/0200", "AES 128/192/256; ECB/CBC/CTR; native padding controls."),
        new("QC3DECDT", "Decrypt data", "12", "ALGD0200, KEYD0100/0200", "AES 128/192/256; ECB/CBC/CTR; optional pad-count removal."),
        new("QC3CRTKX", "Create key context", "9", "Binary clear key; opaque 8-byte token", "Encrypted storage; 256 contexts per job; deleted at job end."),
        new("QC3DESKX", "Destroy key context", "2", "Opaque 8-byte token", "Executing job only; no foreign-job or stale-token access."),
        new("QMHSNDPM", "Send program message", "9, 12, 14", "OPM entry/counter and writable 4-byte key", "Current sends: INFO, COMP, DIAG. Exception/UI delivery is open."),
        new("QMHRCVPM", "Receive program message", "10, 12, 14, 15", "RCVM0100, RCVM0200", "SAME, OLD, REMOVE; OPM entries/counters. No pointer/ILE selection."),
        new("QMHSNDM", "Send nonprogram message", "10, 11", "Qualified queue list and inquiry copy key", "Named queues; immediate text up to 6000 bytes, data up to 512."),
        new("QMHRCVM", "Receive nonprogram message", "9, 10, 11", "RCVM0100, RCVM0200", "Named queues; cancelable waits allocate the receiver queue."),
        new("QMHSNDRM", "Send reply message", "6, 7, 8", "Inquiry key, queue and reply bytes", "Stored reply rules/defaults apply; rejection exits are open."),
        new("QMHRTVM", "Retrieve message description", "10, 13", "RTVM0100", "MSGID, FIRST, NEXT; substitution and help control selection.")
    });
}
