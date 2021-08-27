using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KSPCommunityFixes
{
    class AllowNestedInventoryPart : BasePatch
    {
        private static FieldInfo UIPartActionInventorySlot_moduleInventoryPart;

        protected override void ApplyPatches(ref List<PatchInfo> patches)
        {
            UIPartActionInventorySlot_moduleInventoryPart = AccessTools.Field(typeof(UIPartActionInventorySlot), "moduleInventoryPart");

            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(Part), nameof(Part.AddModule), new[] { typeof(string), typeof(bool) }),
                GetType()));

            patches.Add(new PatchInfo(
                PatchMethodType.Postfix,
                AccessTools.Method(typeof(ModuleInventoryPart), nameof(ModuleInventoryPart.CanStackInSlot)),
                GetType()));

            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(UIPartActionInventorySlot), "ProcessClickWithHeldPart"),
                GetType()));

            patches.Add(new PatchInfo(
                PatchMethodType.Prefix,
                AccessTools.Method(typeof(UIPartActionInventorySlot), "StorePartInEmptySlot"),
                GetType()));
        }

        protected override bool CanApplyPatch(out string reason)
        {
            if (!KSPCommunityFixes.enabledPatches.Contains(nameof(BetterCargoPartVolume)))
            {
                reason = "dependant on the BetterCargoPartVolume patch, which is disabled";
                return false;
            }

            return base.CanApplyPatch(out reason);
        }

        // remove the hardcoded checks that prevent having a ModuleCargoPart and a ModuleInventoryPart on the same part
        static bool Part_AddModule_Prefix(Part __instance, string moduleName, bool forceAwake, ref PartModule __result)
        {
            Type classByName = AssemblyLoader.GetClassByName(typeof(PartModule), moduleName);
            if (classByName == null)
            {
                Debug.LogError("Cannot find a PartModule of typename '" + moduleName + "'");
                __result = null;
                return false;
            }

            PartModule partModule = (PartModule)__instance.gameObject.AddComponent(classByName);
            if (partModule == null)
            {
                Debug.LogError("Cannot create a PartModule of typename '" + moduleName + "'");
                __result = null;
                return false;
            }
            if (forceAwake)
            {
                partModule.Awake();
            }
            __instance.Modules.Add(partModule);
            __result = partModule;
            return false;
        }

        // Make sure inventory parts can't be stacked
        static void ModuleInventoryPart_CanStackInSlot_Postfix(AvailablePart part, ref bool __result)
        {
            if (!__result)
                return;

            if (part.partPrefab.HasModuleImplementing<ModuleInventoryPart>())
            {
                __result = false;
            }
        }

        static bool UIPartActionInventorySlot_ProcessClickWithHeldPart_Prefix(UIPartActionInventorySlot __instance)
        {
            Part inventoryPart = null;
            if (UIPartActionInventorySlot_moduleInventoryPart.GetValue(__instance) is ModuleInventoryPart inventoryModule)
            {
                inventoryPart = inventoryModule.part;
            }

            return CanBeStored(UIPartActionControllerInventory.Instance.CurrentCargoPart, inventoryPart);
        }

        static bool UIPartActionInventorySlot_StorePartInEmptySlot_Prefix(UIPartActionInventorySlot __instance, Part partToStore)
        {
            Part inventoryPart = null;
            if (UIPartActionInventorySlot_moduleInventoryPart.GetValue(__instance) is ModuleInventoryPart inventoryModule)
            {
                inventoryPart = inventoryModule.part;
            }

            return CanBeStored(partToStore, inventoryPart);
        }

		private static bool CanBeStored(Part cargoPart, Part inventoryPart = null)
		{
            // Prevent inventory parts to be stored in themselves
            if (inventoryPart != null && cargoPart == inventoryPart)
                return false;

            return true;
        }
    }
}
