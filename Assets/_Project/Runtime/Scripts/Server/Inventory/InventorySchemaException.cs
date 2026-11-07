using System;

namespace RPG4.Server.Inventory
{
    /// <summary>Fixed inventory schema classification shared with the composition root.</summary>
    public sealed class InventorySchemaException : Exception
    {
        public string Code { get; }
        public InventorySchemaException(string code) : base(code) { Code = code; }
    }
}
