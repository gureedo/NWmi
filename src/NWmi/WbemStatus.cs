namespace NWmi;

/// <summary>WBEMSTATUS (MS-WMI Appendix A, tag_WBEMSTATUS)</summary>
public enum WbemStatus : uint
{
    /// <summary>The operation succeeded (WBEM_S_NO_ERROR).</summary>
    NoError = 0x00000000,

    /// <summary>Success, but there are no more objects or fewer than asked for came back (WBEM_S_FALSE).</summary>
    False = 0x00000001,

    /// <summary>The call timed out; this is not an error (WBEM_S_TIMEDOUT).</summary>
    Timedout = 0x00040004,

    /// <summary>The receiver can take optimized sink calls (WBEM_S_NEW_STYLE).</summary>
    NewStyle = 0x000400FF,

    /// <summary>Only some of the requested results were returned (WBEM_S_PARTIAL_RESULTS).</summary>
    PartialResults = 0x00040010,

    /// <summary>The call failed (WBEM_E_FAILED).</summary>
    Failed = 0x80041001,

    /// <summary>The object, class or namespace was not found (WBEM_E_NOT_FOUND).</summary>
    NotFound = 0x80041002,

    /// <summary>Access is denied (WBEM_E_ACCESS_DENIED).</summary>
    AccessDenied = 0x80041003,

    /// <summary>The provider failed after it initialized (WBEM_E_PROVIDER_FAILURE).</summary>
    ProviderFailure = 0x80041004,

    /// <summary>A type mismatch occurred (WBEM_E_TYPE_MISMATCH).</summary>
    TypeMismatch = 0x80041005,

    /// <summary>There is not enough memory (WBEM_E_OUT_OF_MEMORY).</summary>
    OutOfMemory = 0x80041006,

    /// <summary>The context object is not valid (WBEM_E_INVALID_CONTEXT).</summary>
    InvalidContext = 0x80041007,

    /// <summary>A parameter is not valid (WBEM_E_INVALID_PARAMETER).</summary>
    InvalidParameter = 0x80041008,

    /// <summary>The resource, typically a remote server, is not currently available (WBEM_E_NOT_AVAILABLE).</summary>
    NotAvailable = 0x80041009,

    /// <summary>An internal, critical and unexpected error occurred (WBEM_E_CRITICAL_ERROR).</summary>
    CriticalError = 0x8004100A,

    /// <summary>The superclass is not valid (WBEM_E_INVALID_SUPERCLASS).</summary>
    InvalidSuperclass = 0x8004100D,

    /// <summary>The namespace could not be found (WBEM_E_INVALID_NAMESPACE).</summary>
    InvalidNamespace = 0x8004100E,

    /// <summary>The instance is not valid (WBEM_E_INVALID_OBJECT).</summary>
    InvalidObject = 0x8004100F,

    /// <summary>The class is not valid (WBEM_E_INVALID_CLASS).</summary>
    InvalidClass = 0x80041010,

    /// <summary>A provider named in the schema has no registration (WBEM_E_PROVIDER_NOT_FOUND).</summary>
    ProviderNotFound = 0x80041011,

    /// <summary>A provider's registration is incorrect or incomplete (WBEM_E_INVALID_PROVIDER_REGISTRATION).</summary>
    InvalidProviderRegistration = 0x80041012,

    /// <summary>COM cannot locate a provider named in the schema (WBEM_E_PROVIDER_LOAD_FAILURE).</summary>
    ProviderLoadFailure = 0x80041013,

    /// <summary>A component, such as a provider, failed to initialize (WBEM_E_INITIALIZATION_FAILURE).</summary>
    InitializationFailure = 0x80041014,

    /// <summary>A networking error stopped normal operation (WBEM_E_TRANSPORT_FAILURE).</summary>
    TransportFailure = 0x80041015,

    /// <summary>The requested operation is not valid (WBEM_E_INVALID_OPERATION).</summary>
    InvalidOperation = 0x80041016,

    /// <summary>The query is not valid (WBEM_E_INVALID_QUERY).</summary>
    InvalidQuery = 0x80041017,

    /// <summary>The query language is not supported (WBEM_E_INVALID_QUERY_TYPE).</summary>
    InvalidQueryType = 0x80041018,

    /// <summary>The object already exists (WBEM_E_ALREADY_EXISTS).</summary>
    AlreadyExists = 0x80041019,

    /// <summary>The requested operation is not supported (WBEM_E_NOT_SUPPORTED).</summary>
    NotSupported = 0x8004100C,

    /// <summary>An unexpected error occurred (WBEM_E_UNEXPECTED).</summary>
    Unexpected = 0x8004101D,

    /// <summary>The class is incomplete, for example it has no key (WBEM_E_INCOMPLETE_CLASS).</summary>
    IncompleteClass = 0x80041020,

    /// <summary>The provider does not support the requested operation (WBEM_E_PROVIDER_NOT_CAPABLE).</summary>
    ProviderNotCapable = 0x80041024,

    /// <summary>The class has subclasses, so it cannot be changed or deleted this way (WBEM_E_CLASS_HAS_CHILDREN).</summary>
    ClassHasChildren = 0x80041025,

    /// <summary>The class has instances, so it cannot be changed or deleted this way (WBEM_E_CLASS_HAS_INSTANCES).</summary>
    ClassHasInstances = 0x80041026,

    /// <summary>A property that cannot be null was set to null (WBEM_E_ILLEGAL_NULL).</summary>
    IllegalNull = 0x80041028,

    /// <summary>The CIM type is not valid (WBEM_E_INVALID_CIM_TYPE).</summary>
    InvalidCimType = 0x8004102D,

    /// <summary>The method is not valid (WBEM_E_INVALID_METHOD).</summary>
    InvalidMethod = 0x8004102E,

    /// <summary>The method's parameters are not valid (WBEM_E_INVALID_METHOD_PARAMETERS).</summary>
    InvalidMethodParameters = 0x8004102F,

    /// <summary>The property is not valid (WBEM_E_INVALID_PROPERTY).</summary>
    InvalidProperty = 0x80041031,

    /// <summary>The call was cancelled (WBEM_E_CALL_CANCELLED).</summary>
    CallCancelled = 0x80041032,

    /// <summary>WMI is shutting down (WBEM_E_SHUTTING_DOWN).</summary>
    ShuttingDown = 0x80041033,

    /// <summary>The object path is not valid (WBEM_E_INVALID_OBJECT_PATH).</summary>
    InvalidObjectPath = 0x8004103A,

    /// <summary>There is not enough disk space (WBEM_E_OUT_OF_DISK_SPACE).</summary>
    OutOfDiskSpace = 0x8004103B,

    /// <summary>The put operation does not support the requested extension (WBEM_E_UNSUPPORTED_PUT_EXTENSION).</summary>
    UnsupportedPutExtension = 0x8004103D,

    /// <summary>A quota was exceeded (WBEM_E_QUOTA_VIOLATION).</summary>
    QuotaViolation = 0x8004106C,

    /// <summary>The server is too busy to take the request (WBEM_E_SERVER_TOO_BUSY).</summary>
    ServerTooBusy = 0x80041045,

    /// <summary>The method is declared but not implemented (WBEM_E_METHOD_NOT_IMPLEMENTED).</summary>
    MethodNotImplemented = 0x80041055,

    /// <summary>The method is disabled (WBEM_E_METHOD_DISABLED).</summary>
    MethodDisabled = 0x80041056,

    /// <summary>The query cannot be parsed (WBEM_E_UNPARSABLE_QUERY).</summary>
    UnparsableQuery = 0x80041058,

    /// <summary>The class in an event query is not an event class (WBEM_E_NOT_EVENT_CLASS).</summary>
    NotEventClass = 0x80041059,

    /// <summary>An event query that groups needs a GROUP WITHIN clause (WBEM_E_MISSING_GROUP_WITHIN).</summary>
    MissingGroupWithin = 0x8004105A,

    /// <summary>An event query that groups needs an aggregation list (WBEM_E_MISSING_AGGREGATION_LIST).</summary>
    MissingAggregationList = 0x8004105B,

    /// <summary>A property used in the event query is not an object (WBEM_E_PROPERTY_NOT_AN_OBJECT).</summary>
    PropertyNotAnObject = 0x8004105C,

    /// <summary>The event query aggregates by an object property (WBEM_E_AGGREGATING_BY_OBJECT).</summary>
    AggregatingByObject = 0x8004105D,

    /// <summary>Backup or restore needs the WMI service to be stopped (WBEM_E_BACKUP_RESTORE_WINMGMT_RUNNING).</summary>
    BackupRestoreWinmgmtRunning = 0x80041060,

    /// <summary>The event queue overflowed (WBEM_E_QUEUE_OVERFLOW).</summary>
    QueueOverflow = 0x80041061,

    /// <summary>A privilege the operation needs is not held (WBEM_E_PRIVILEGE_NOT_HELD).</summary>
    PrivilegeNotHeld = 0x80041062,

    /// <summary>The operator is not valid for the property (WBEM_E_INVALID_OPERATOR).</summary>
    InvalidOperator = 0x80041063,

    /// <summary>The class cannot be made abstract (WBEM_E_CANNOT_BE_ABSTRACT).</summary>
    CannotBeAbstract = 0x80041065,

    /// <summary>The object is an amended (localized) one and cannot be written back (WBEM_E_AMENDED_OBJECT).</summary>
    AmendedObject = 0x80041066,

    /// <summary>A provider vetoed the put (WBEM_E_VETO_PUT).</summary>
    VetoPut = 0x8004107A,

    /// <summary>The provider is suspended (WBEM_E_PROVIDER_SUSPENDED).</summary>
    ProviderSuspended = 0x80041081,

    /// <summary>The namespace requires an encrypted connection (WBEM_E_ENCRYPTED_CONNECTION_REQUIRED).</summary>
    EncryptedConnectionRequired = 0x80041087,

    /// <summary>The provider did not answer in time (WBEM_E_PROVIDER_TIMED_OUT).</summary>
    ProviderTimedOut = 0x80041088,

    /// <summary>The class has no key property (WBEM_E_NO_KEY).</summary>
    NoKey = 0x80041089,

    /// <summary>The provider is disabled (WBEM_E_PROVIDER_DISABLED).</summary>
    ProviderDisabled = 0x8004108A,

    /// <summary>The provider's registration overlaps another (WBEM_E_REGISTRATION_TOO_BROAD).</summary>
    RegistrationTooBroad = 0x80042001,

    /// <summary>The provider's registration is too precise (WBEM_E_REGISTRATION_TOO_PRECISE).</summary>
    RegistrationTooPrecise = 0x80042002,

    /// <summary>The operation is not implemented (E_NOTIMPL).</summary>
    NotImplemented = 0x80004001,
}
