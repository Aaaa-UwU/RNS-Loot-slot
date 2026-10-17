using RNSReloaded.Interfaces;
using RNSReloaded.Interfaces.Structs;

namespace RnSLootSlots;

/// <summary>
/// Thin, null-tolerant readers over the game's runtime state. Mirrors the access patterns proven in
/// RnSElites: globals are read by name off the global instance, instance fields by name off a
/// <c>CInstance</c>, and every read that can fail returns a fallback instead of throwing — a mod must
/// never let an exception escape into the game's stack.
/// </summary>
public unsafe sealed class GameRefs {
    private readonly IRNSReloaded rns;

    public GameRefs(IRNSReloaded rns) {
        this.rns = rns;
    }

    /// <summary>Reads a global by name, or null if it does not exist.</summary>
    public RValue* Global(string name) {
        try {
            return this.rns.FindValue(this.rns.GetGlobalInstance(), name);
        } catch {
            return null;
        }
    }

    /// <summary>Reads a global by name as a double, or <paramref name="fallback"/> if absent.</summary>
    public double ReadGlobalDouble(string name, double fallback = 0) {
        var value = this.Global(name);
        return value == null ? fallback : ToDouble(value);
    }

    /// <summary>Reads an instance field, or null if the instance does not have one by that name.</summary>
    public RValue* Field(CInstance* instance, string field) {
        if (instance == null) return null;
        try {
            return this.rns.FindValue(instance, field);
        } catch {
            return null;
        }
    }

    /// <summary>Reads an instance field as a long, or <paramref name="fallback"/> if absent.</summary>
    public long ReadLong(CInstance* instance, string field, long fallback = -1) {
        if (instance == null) return fallback;
        try {
            var value = this.rns.FindValue(instance, field);
            return value == null ? fallback : (long) ToDouble(value);
        } catch {
            return fallback;
        }
    }

    /// <summary>
    /// Whether the instance has a field at all. A field that exists but reads as 0 is different from a
    /// field an object does not have, and the probe logs need to tell those apart.
    /// </summary>
    public bool HasField(CInstance* instance, string field) {
        if (instance == null) return false;
        try {
            return this.rns.FindValue(instance, field) != null;
        } catch {
            return false;
        }
    }

    /// <summary>All field names on an instance, for probe dumps.</summary>
    public List<string> StructKeys(CInstance* instance) {
        if (instance == null) return [];
        try {
            var wrapper = new RValue(instance);
            return this.rns.GetStructKeys(&wrapper);
        } catch {
            return [];
        }
    }

    /// <summary>Number of entries in an array RValue, or 0.</summary>
    public int ArrayLength(RValue* array) {
        if (array == null) return 0;
        try {
            var length = this.rns.ArrayGetLength(array);
            if (!length.HasValue) return 0;
            return length.Value.Type switch {
                RValueType.Real => (int) length.Value.Real,
                RValueType.Int32 => length.Value.Int32,
                RValueType.Int64 => (int) length.Value.Int64,
                _ => 0,
            };
        } catch {
            return 0;
        }
    }

    /// <summary>Reads a string RValue, or null when it is not a string or cannot be read.</summary>
    public string? SafeString(RValue* value) {
        if (value == null || value->Type != RValueType.String) return null;
        try {
            return this.rns.GetString(value);
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Calls a game script with arguments and returns its result as a double, or null if the call
    /// failed. Exceptions are swallowed: a mod must never let one escape into the game's stack.
    /// </summary>
    public double? CallScript(string name, CInstance* self, CInstance* other, params double[] args) {
        try {
            var values = args.Select(a => new RValue(a)).ToArray();
            var result = this.rns.ExecuteScript(name, self, other, values);
            if (result is null) return null;

            // Copy out of the nullable first: you cannot take the address of a property.
            var value = result.Value;
            return ToDouble(&value);
        } catch {
            return null;
        }
    }

    public static double ToDouble(RValue* value) {
        if (value == null) return 0;
        return value->Type switch {
            RValueType.Real => value->Real,
            RValueType.Int32 => value->Int32,
            RValueType.Int64 => value->Int64,
            RValueType.Bool => value->Bool ? 1 : 0,
            _ => 0,
        };
    }
}
