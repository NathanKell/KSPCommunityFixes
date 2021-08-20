using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace KSPCommunityFixes
{
    class AllowNestedEmptyInventoryPart : BasePatch
    {
        protected override void ApplyPatches(ref List<PatchInfo> patches)
        {
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

        static void ModuleInventoryPart_CanStackInSlot_Postfix(AvailablePart part, ref bool __result)
        {
            if (!__result)
                return;

            if (!CanBeStored(part.partPrefab))
            {
                __result = false;
            }
        }

        static bool UIPartActionInventorySlot_ProcessClickWithHeldPart_Prefix()
        {
            return CanBeStored(UIPartActionControllerInventory.Instance.CurrentCargoPart);
        }

        static bool UIPartActionInventorySlot_StorePartInEmptySlot_Prefix(Part partToStore)
        {
            return CanBeStored(partToStore);
        }

		private static bool CanBeStored(Part part)
		{
			ModuleInventoryPart inventory = part.FindModuleImplementing<ModuleInventoryPart>();

			if (inventory == null || inventory.InventoryIsEmpty)
				return true;

			ScreenMessages.PostScreenMessage($"Can't store {part.partInfo.title}\nIts inventory must be emptied first.", 5f, ScreenMessageStyle.UPPER_CENTER);

			return false;
		}


	}
}
