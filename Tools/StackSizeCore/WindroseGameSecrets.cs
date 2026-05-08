namespace Windrose.StackSize.Core
{
    // Shared constants used by the dump + icon pipelines.
    // The AES key is the public game key (NOT a secret -- it's used by
    // every Windrose modding tool, e.g. WindrosePlus' IniConfigParser).
    public static class WindroseGameSecrets
    {
        public const string AesKey =
            "0x5F430BF9FEF2B0B91B7C79C313BDAF291BA076A1DAB5045974186333AA16CFAE";

        // Pak-internal prefix the dumper extracts.
        public const string InventoryItemsPath =
            "R5/Plugins/R5BusinessRules/Content/InventoryItems";
    }
}
