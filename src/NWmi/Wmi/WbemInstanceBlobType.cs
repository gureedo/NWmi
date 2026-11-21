namespace NWmi.Wmi;

/// <summary>WBEM_INSTANCE_BLOB_TYPE (MS-WMI 2.2.17): the kind of CIM object a refreshed object's blob holds.</summary>
internal enum WbemInstanceBlobType
{
    /// <summary>WBEM_BLOB_TYPE_ALL: a single CIM object.</summary>
    All = 2,

    /// <summary>WBEM_BLOB_TYPE_ERROR: an error condition; the object is null.</summary>
    Error = 3,

    /// <summary>WBEM_BLOB_TYPE_ENUM: an enumeration of objects of one CIM type.</summary>
    Enum = 4,
}
