if (-not ('PeggyMsiNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class PeggyMsiNative
{
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorUnknownProduct = 1605;

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);

    [DllImport("msi.dll", EntryPoint = "MsiViewExecute")]
    private static extern uint MsiViewExecute(uint view, uint record);

    [DllImport("msi.dll", EntryPoint = "MsiViewFetch")]
    private static extern uint MsiViewFetch(uint view, out uint record);

    [DllImport("msi.dll", EntryPoint = "MsiRecordGetFieldCount")]
    private static extern uint MsiRecordGetFieldCount(uint record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
    private static extern uint MsiRecordGetString(
        uint record,
        uint field,
        StringBuilder value,
        ref uint length);

    [DllImport("msi.dll", EntryPoint = "MsiRecordReadStream")]
    private static extern uint MsiRecordReadStream(
        uint record,
        uint field,
        [Out] byte[] buffer,
        ref uint length);

    [DllImport("msi.dll", EntryPoint = "MsiDatabaseCommit")]
    private static extern uint MsiDatabaseCommit(uint database);

    [DllImport("msi.dll", EntryPoint = "MsiCloseHandle")]
    private static extern uint MsiCloseHandle(uint handle);

    [DllImport("msi.dll", EntryPoint = "MsiGetSummaryInformationW", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetSummaryInformation(
        uint database,
        string databasePath,
        uint updateCount,
        out uint summary);

    [DllImport("msi.dll", EntryPoint = "MsiSummaryInfoGetPropertyW", CharSet = CharSet.Unicode)]
    private static extern uint MsiSummaryInfoGetProperty(
        uint summary,
        uint property,
        out uint dataType,
        out int integerValue,
        out long fileTime,
        StringBuilder value,
        ref uint valueLength);

    [DllImport("msi.dll", EntryPoint = "MsiSummaryInfoSetPropertyW", CharSet = CharSet.Unicode)]
    private static extern uint MsiSummaryInfoSetProperty(
        uint summary,
        uint property,
        uint dataType,
        int integerValue,
        IntPtr fileTime,
        string value);

    [DllImport("msi.dll", EntryPoint = "MsiSummaryInfoPersist")]
    private static extern uint MsiSummaryInfoPersist(uint summary);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumRelatedProductsW")]
    private static extern uint MsiEnumRelatedProducts(
        string upgradeCode,
        uint context,
        uint index,
        StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiQueryProductStateW")]
    private static extern int MsiQueryProductState(string productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoW")]
    private static extern uint MsiGetProductInfo(
        string productCode,
        string property,
        StringBuilder value,
        ref uint valueLength);

    public static IntPtr OpenDatabase(string path, int mode)
    {
        Check(MsiOpenDatabase(path, new IntPtr(mode), out var database));
        return new IntPtr(database);
    }

    public static IntPtr OpenView(IntPtr database, string query)
    {
        Check(MsiDatabaseOpenView(ToHandle(database), query, out var view));
        return new IntPtr(view);
    }

    public static void ExecuteView(IntPtr view)
        => Check(MsiViewExecute(ToHandle(view), 0));

    public static bool Fetch(IntPtr view, out IntPtr record)
    {
        var result = MsiViewFetch(ToHandle(view), out var recordHandle);
        if (result == ErrorNoMoreItems)
        {
            record = IntPtr.Zero;
            return false;
        }
        Check(result);
        record = new IntPtr(recordHandle);
        return true;
    }

    public static int FieldCount(IntPtr record)
        => checked((int)MsiRecordGetFieldCount(ToHandle(record)));

    public static string RecordString(IntPtr record, int field)
    {
        var value = new StringBuilder(32768);
        uint length = (uint)(value.Capacity - 1);
        Check(MsiRecordGetString(ToHandle(record), (uint)field, value, ref length));
        return value.ToString();
    }

    public static byte[] ReadStreamChunk(IntPtr record, int field, int bufferSize)
    {
        var buffer = new byte[bufferSize];
        uint length = (uint)buffer.Length;
        Check(MsiRecordReadStream(ToHandle(record), (uint)field, buffer, ref length));
        if (length == buffer.Length)
        {
            return buffer;
        }
        Array.Resize(ref buffer, checked((int)length));
        return buffer;
    }

    public static void Commit(IntPtr database)
        => Check(MsiDatabaseCommit(ToHandle(database)));

    public static void Close(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            Check(MsiCloseHandle(ToHandle(handle)));
        }
    }

    public static IntPtr OpenSummary(IntPtr database, int updateCount)
    {
        Check(MsiGetSummaryInformation(
            ToHandle(database),
            null,
            (uint)updateCount,
            out var summary));
        return new IntPtr(summary);
    }

    public static object SummaryProperty(IntPtr summary, int property)
    {
        var value = new StringBuilder(32768);
        uint length = (uint)(value.Capacity - 1);
        var result = MsiSummaryInfoGetProperty(
            ToHandle(summary),
            (uint)property,
            out var dataType,
            out var integerValue,
            out _,
            value,
            ref length);
        if (result == 0)
        {
            return dataType switch
            {
                2 => (short)integerValue,
                3 => integerValue,
                30 => value.ToString(),
                _ => throw new InvalidDataException(
                    $"Unsupported MSI summary property type {dataType}.")
            };
        }
        if (result == 1608)
        {
            return null;
        }
        Check(result);
        return null;
    }

    public static void SetSummaryString(IntPtr summary, int property, string value)
        => Check(MsiSummaryInfoSetProperty(
            ToHandle(summary),
            (uint)property,
            30,
            0,
            IntPtr.Zero,
            value));

    public static void SetSummaryInteger(IntPtr summary, int property, int value)
        => Check(MsiSummaryInfoSetProperty(
            ToHandle(summary),
            (uint)property,
            3,
            value,
            IntPtr.Zero,
            null));

    public static void PersistSummary(IntPtr summary)
        => Check(MsiSummaryInfoPersist(ToHandle(summary)));

    public static string[] RelatedProducts(string upgradeCode)
    {
        var productCodes = new System.Collections.Generic.List<string>();
        for (uint index = 0; ; index++)
        {
            var productCode = new StringBuilder(39);
            var result = MsiEnumRelatedProducts(
                upgradeCode,
                0,
                index,
                productCode);
            if (result == ErrorNoMoreItems)
            {
                return productCodes.ToArray();
            }
            Check(result);
            productCodes.Add(productCode.ToString());
        }
    }

    public static int ProductState(string productCode)
        => MsiQueryProductState(productCode);

    public static string ProductInfo(string productCode, string property)
    {
        var value = new StringBuilder(32768);
        uint length = (uint)(value.Capacity - 1);
        var result = MsiGetProductInfo(
            productCode,
            property,
            value,
            ref length);
        if (result == ErrorUnknownProduct)
        {
            return null;
        }
        Check(result);
        return value.ToString();
    }

    private static void Check(uint result)
    {
        if (result != 0)
        {
            throw new Win32Exception(
                checked((int)result),
                $"Windows Installer API failed with error {result}.");
        }
    }

    private static uint ToHandle(IntPtr handle)
        => checked((uint)handle.ToInt64());
}
'@
}
