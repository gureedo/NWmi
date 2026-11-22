namespace NWmi;

/// <summary>WBEM_CHANGE_FLAG_TYPE: the <c>lFlags</c> bits <c>PutInstance</c>/<c>PutClass</c> accept.</summary>
[Flags]
public enum WbemChangeFlags : uint
{
    /// <summary>Create the object if it is new, otherwise update it (WBEM_FLAG_CREATE_OR_UPDATE).</summary>
    CreateOrUpdate = 0x00,

    /// <summary>Update an object that exists; fail if it does not (WBEM_FLAG_UPDATE_ONLY).</summary>
    UpdateOnly = 0x01,

    /// <summary>Create a new object; fail if it exists (WBEM_FLAG_CREATE_ONLY).</summary>
    CreateOnly = 0x02,

    /// <summary>Update a class only if it will not break its subclasses or instances (WBEM_FLAG_UPDATE_SAFE_MODE).</summary>
    UpdateSafeMode = 0x20,

    /// <summary>Update a class even if that deletes its subclasses or instances (WBEM_FLAG_UPDATE_FORCE_MODE).</summary>
    UpdateForceMode = 0x40,
}
