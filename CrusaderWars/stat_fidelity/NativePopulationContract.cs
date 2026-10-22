using System.IO;

namespace CrusaderWars.StatFidelity;

public static class NativePopulationContract
{
    public static void ValidateEquipmentMapping(NativeMaaDefinition native, bool mappedAsEquipment)
    {
        if (mappedAsEquipment && !native.IsSiegeEquipment)
            throw new InvalidDataException("Native type '" + native.Key + "' (" + (native.Category ?? "unknown category") +
                ") does not declare an equipment population. An artillery/manual appearance cannot convert its people into machines; choose a compatible troop template.");
    }
}
