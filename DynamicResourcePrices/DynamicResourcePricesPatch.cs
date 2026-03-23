//FILE: DynamicResourcePricesPatch.cs

using HarmonyLib;
using DV;
using DV.Logic;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.TimeKeeping;
using DV.WeatherSystem;
using DV.ServicePenalty;
using System;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace DynamicResourcePrices
{	
	[HarmonyPatch(typeof(WorldStreamingInit), "Awake")]
	static class InitPatch
	{
		static void Postfix()
		{
			GameObject go = new GameObject("DynamicResourcePricesController");
			go.AddComponent<DynamicResourcePricesMain.DynamicResourcePricesWatcher>();
			UnityEngine.Object.DontDestroyOnLoad(go);
		}
	}
		
    [HarmonyPatch(typeof(SaveGameManager), "FindStartGameData")]
    static class Patch_SaveGameManager_Load
    {
        static void Postfix(SaveGameManager __instance)
        {
            try
            {
                var inst = DynamicResourcePricesMain.Instance;

                if (inst == null)
                {
					if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
					{
						Debug.LogWarning("[DynamicResourcePrices] SaveGameManager → Instance NULL");
					}
                    return;
                }

                var data = __instance.data;

                if (data == null)
                {
					if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
					{
						Debug.LogWarning("[DynamicResourcePrices] SaveGameManager : data NULL");
					}
                    return;
                }

                JObject root = data.GetJsonObject();

                if (root == null)
                {
					if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
					{
						Debug.LogWarning("[DynamicResourcePrices] SaveGameManager : root NULL");
					}
                    return;
                }

				inst.HardResetState();

				if (root.TryGetValue("DynamicResourcePrices-Mod", out JToken? block))
				{
					inst.LoadFromSave((JObject)block);
				}
				else
				{
					if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
					{
						Debug.Log("[DynamicResourcePrices] SaveGameManager : NEW SAVE DETECTED - RESET ECONOMY");
					}

					inst.ResetEconomy();
				}

				inst.MarkSaveLoaded();

				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.Log("[DynamicResourcePrices] SaveGameManager : LOAD COMPLETE");
				}
            }
            catch (System.Exception ex)
            {
				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.LogError("[DynamicResourcePrices] SaveGameManager Patch ERROR: " + ex);
				}
            }
        }
    }

	[HarmonyPatch]
	public static class PitStopStationPatch
	{
		static System.Reflection.MethodBase TargetMethod()
		{
			return AccessTools.Method("PitStopStation:OnCarPitStopSelected");
		}

		static void Postfix(object __instance)
		{
			try
			{
				if (DynamicResourcePricesMain.Instance == null)
					return;

				var pitstopField = AccessTools.Field(__instance.GetType(), "pitstop");
				var pitstop = pitstopField?.GetValue(__instance);

				if (pitstop == null)
					return;

				bool isInPit = (bool)AccessTools.Method(pitstop.GetType(), "IsCarInPitStop")
					.Invoke(pitstop, null);

				if (!isInPit)
					return;

				var car = AccessTools.Property(pitstop.GetType(), "SelectedCar")
					.GetValue(pitstop);

				if (car == null)
					return;

				var indicatorsField = AccessTools.Field(__instance.GetType(), "locoResourceModules");
				var indicators = indicatorsField?.GetValue(__instance);

				if (indicators == null)
					return;

				var indType = indicators.GetType();

				var updateIndep = AccessTools.Method(indType, "UpdateIndependentPrices");
				var updateType = AccessTools.Method(indType, "UpdatePricesDependingOnLocoType");

				object? carLivery = null;

				var carLiveryProp = car.GetType().GetProperty("carLivery");

				if (carLiveryProp != null)
				{
					carLivery = carLiveryProp.GetValue(car);
				}

				updateIndep?.Invoke(indicators, new object[] { car });

				if (updateType != null && carLivery != null)
					updateType.Invoke(indicators, new object[] { car, carLivery });

				//Debug.Log("[DynamicResourcePrices] PitStopStation patched update applied");
			}
			catch (System.Exception ex)
			{
				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.LogError("[DynamicResourcePrices] PitStopStation patch error: " + ex);
				}
			}
		}
	}
	
    [HarmonyPatch(typeof(ResourceTypes), "GetFullUnitPriceOfResource")]
    public static class DynamicResourcePricesPatch
    {
        static bool Prefix(ResourceType resource, ref float __result)
        {
            if (DynamicResourcePricesMain.Instance == null)
                return true;

            if (resource != ResourceType.Fuel &&
                resource != ResourceType.Coal &&
                resource != ResourceType.ElectricCharge)
            {
                return true;
            }

            float dynamicPrice = DynamicResourcePricesMain.Instance.GetPrice(resource);

            if (dynamicPrice > 0f)
            {
                __result = dynamicPrice;
                //Debug.Log("[DynamicResourcePrices] OVERRIDE " + resource + " → " + dynamicPrice);
                return false;
            }

            return true;
        }
    }	
	
	
    [HarmonyPatch(typeof(WeatherDriver), "LoadSaveData")]
    public static class Patch_WeatherLoad
    {
        static void Postfix(WeatherDriver __instance)
        {
            try
            {
                var inst = DynamicResourcePricesMain.Instance;

                if (inst == null)
                {
					if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
					{
						Debug.LogWarning("[DynamicResourcePrices] WeatherLoad → Instance NULL");
					}
                    return;
                }

                var manager = __instance.manager;

                if (manager == null)
                {
					if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
					{
						Debug.LogWarning("[DynamicResourcePrices] WeatherLoad → manager NULL");
					}
                    return;
                }

                DateTime time = manager.DateTime;

				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.Log("[DynamicResourcePrices] WeatherLoad → INIT with time: " + time);
				}
            }
            catch (Exception ex)
            {
				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.LogError("[DynamicResourcePrices] WeatherLoad Patch ERROR: " + ex);
				}
            }
        }
    }
	
    [HarmonyPatch(typeof(SaveGameManager), "Save")]
    static class Patch_Save
    {
        static void Prefix()
        {
            var saveData = SaveGameManager.Instance?.data;
            if (saveData == null) return;

            var trav = Traverse.Create(saveData).Field("dataObject");
            var dataObject = trav.GetValue<JObject>() ?? new JObject();

            if (trav.GetValue<JObject>() == null)
                trav.SetValue(dataObject);

            if (DynamicResourcePricesMain.Instance == null)
                return;

            var block = DynamicResourcePricesMain.Instance.GetSaveData();

            dataObject["DynamicResourcePrices-Mod"] = block;

            UnityEngine.Debug.Log("[DynamicResourcePrices] Saved data to savegame");
        }
    } 
	
	[HarmonyPatch(typeof(WarehouseMachine), "UnloadOneCarOfTask")]
	static class Patch_WarehouseUnload
	{
		static void Postfix(WarehouseTask task, Car __result)
		{
			try
			{
				if (__result == null)
					return;

				var inst = DynamicResourcePricesMain.Instance;
				if (inst == null)
					return;

				// -----------------------------
				// RESOURCE
				// -----------------------------
				CargoType cargoType = task.cargoType;

				string? resource = MapCargoToResource(cargoType);
				if (resource == null)
					return;

				// -----------------------------
				// JOB RESOLVE
				// -----------------------------
				string jobId = "UNKNOWN";

				try
				{
					var job = JobsManager.Instance?.GetJobOfCar(__result);

					if (job != null)
					{
						jobId = job.ID;
					}
					else
					{
						var jobField = AccessTools.Field(typeof(Task), "job");
						var fallbackJob = jobField?.GetValue(task) as Job;

						if (fallbackJob != null)
							jobId = fallbackJob.ID;
					}
				}
				catch { }

				// -----------------------------
				// MAP CAR → JOB
				// -----------------------------
				try
				{
					if (TrainCarRegistry.Instance.logicCarToTrainCar.TryGetValue(__result, out var trainCar))
					{
						if (trainCar != null)
						{
							string carId = trainCar.ID;

							inst.RegisterCarToJob(carId, jobId);

							if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
							{
								//Debug.Log($"[DynamicResourcePrices] MAP → {carId} → {jobId}");
							}
						}
					}
				}
				catch { }

				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					//Debug.Log($"[DynamicResourcePrices] JOB RESOLVE → {jobId}");
				}

				// -----------------------------
				// LAST WAGON DETECTION
				// -----------------------------
				bool isLastWagon = false;

				try
				{
					float remaining = task.cars.Sum(c => c.LoadedCargoAmount);

					if (remaining <= 0.01f)
					{
						isLastWagon = true;
					}
				}
				catch { }

				// -----------------------------
				// DELAYED FINALIZE
				// -----------------------------
				if (isLastWagon)
				{
					if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
					{
						//Debug.Log("[DynamicResourcePrices] LAST WAGON → delayed finalize triggered");
					}

					inst.StartCoroutine(inst.DelayedFinalize(inst));
				}
			}
			catch (Exception ex)
			{
				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.LogError("[DynamicResourcePrices] Warehouse patch error: " + ex);
				}
			}
		}

		private static string? MapCargoToResource(CargoType type)
		{
			switch (type)
			{
				case CargoType.CrudeOil:
				case CargoType.Diesel:
					return "Fuel";

				case CargoType.Coal:
					return "Coal";

				default:
					return null;
			}
		}
	}
}