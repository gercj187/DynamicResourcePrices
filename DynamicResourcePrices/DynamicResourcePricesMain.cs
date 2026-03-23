//FILE: DynamicResourcePricesMain.cs

using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityModManagerNet;
using UnityEngine;
using HarmonyLib;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.TimeKeeping;
using DV.WeatherSystem;
using DV.ServicePenalty;

namespace DynamicResourcePrices
{	
    public static class Main
    {
        private static GameObject controllerObject = null!;
        private static DynamicResourcePricesSettings settings = null!;

        public static bool Load(UnityModManager.ModEntry modEntry)
		{
			settings = UnityModManager.ModSettings.Load<DynamicResourcePricesSettings>(modEntry);

			if (settings == null)
			{
				settings = new DynamicResourcePricesSettings();
				settings.Save(modEntry);

				Debug.LogError("[DynamicResourcePrices] SETTINGS LOAD FAILED → created NEW file");
			}
			else
			{
				if (settings.EnableDebugLogs)
				{
					Debug.Log("[DynamicResourcePrices] SETTINGS LOADED SUCCESSFULLY");
					Debug.Log("[DynamicResourcePrices] Interval from file: " + settings.DailyUpdateInterval);
				}
			}

			controllerObject = new GameObject("DynamicResourcePricesMain");

			DynamicResourcePricesMain.Settings = settings;
			var comp = controllerObject.AddComponent<DynamicResourcePricesMain>();

			UnityEngine.Object.DontDestroyOnLoad(controllerObject);

			modEntry.OnGUI = (entry) => settings.Draw();
			modEntry.OnSaveGUI = (entry) => settings.Save(entry);

			var harmony = new Harmony(modEntry.Info.Id);
			harmony.PatchAll();

			
			if (settings.EnableDebugLogs)
			{				
				Debug.Log("[DynamicResourcePrices] Harmony patches applied");
			}
			Debug.Log("[DynamicResourcePrices] Loaded successfully");

			return true;
		}
    }
	
    public class DynamicResourcePricesMain : MonoBehaviour
    {
        public static DynamicResourcePricesMain Instance = null!;
        public static DynamicResourcePricesSettings Settings = null!;
		public bool HasSaveData { get; private set; } = false;
		private bool waitingForSaveLoad = true;

        private Dictionary<ResourceType, float> currentPrices = new Dictionary<ResourceType, float>();
        private Dictionary<ResourceType, float> basePrices = new Dictionary<ResourceType, float>();
        private Dictionary<ResourceType, float> currentTrends = new Dictionary<ResourceType, float>();
		
		private Dictionary<string, string> carToJobCache = new Dictionary<string, string>();
		private Dictionary<string, AggregatedCargoEvent> jobEventBuffer = new();
		private Dictionary<Car, float> cachedCargoHealth = new();
		
		private Dictionary<string, string> carIdToJobId = new Dictionary<string, string>();// NEW
		private Dictionary<ResourceType, float> startPrices = new();
		
		private List<CargoEvent> cargoEvents = new List<CargoEvent>();
		private HashSet<string> processedJobEvents = new HashSet<string>();
		
		private int lastDay;
		private float hoursPerInterval;
		
		private bool isInitialized = false;
		private int lastIntervalIndex = -1;
		private bool hasUsedSaveTrend = false;
		private int lastKnownInterval;

        private void Awake()
		{
			Instance = this;
			
			if (Settings == null)
			{
				Debug.LogError("[DynamicResourcePrices] FATAL: Settings still NULL in Awake!");
				Settings = new DynamicResourcePricesSettings();
			}
			else
			{
				if (Settings.EnableDebugLogs)
				{
					Debug.Log("[DynamicResourcePrices] Settings OK in Awake: " + Settings.DailyUpdateInterval);
				}
			}

			InitBasePrices();
			RecalculateInterval();
		}
		
		public JObject GetSaveData()
		{
			var root = new JObject();

			root["Power"] = CreateResourceBlock(ResourceType.ElectricCharge);
			root["Fuel"] = CreateResourceBlock(ResourceType.Fuel);
			root["Coal"] = CreateResourceBlock(ResourceType.Coal);
			root["Events"] = CreateEventsArray();

			return root;
		}

		private JObject CreateResourceBlock(ResourceType type)
		{
			float trend = currentTrends.ContainsKey(type) ? currentTrends[type] * 100f : 0f;
			float price = currentPrices.ContainsKey(type) ? currentPrices[type] : 0f;

			return new JObject
			{
				["Trend"] = trend,
				["Price"] = price
			};
		}
		
		public void MarkSaveLoaded()
		{
			waitingForSaveLoad = false;
		}
		
		
		
		public void LoadFromSave(JObject data)
		{
			if (data == null)
			{
				if (Settings.EnableDebugLogs)
				{
					Debug.LogWarning("[DynamicResourcePrices] LoadFromSave called with NULL ? fallback to new economy");
				}

				ResetEconomy();
				return;
			}

			HardResetState();

			HasSaveData = true;
			hasUsedSaveTrend = true;

			LoadResource(data, "Power", ResourceType.ElectricCharge);
			LoadResource(data, "Fuel", ResourceType.Fuel);
			LoadResource(data, "Coal", ResourceType.Coal);

			if (Settings.EnableDebugLogs)
			{
				Debug.Log("[DynamicResourcePrices] Loaded prices & trends from savegame (clean)");
			}

			if (data.TryGetValue("Events", out JToken? evToken))
			{
				var arr = evToken as JArray;

				if (arr != null && arr.Count > 0)
				{
					var startBlock = arr[0] as JObject;

					if (startBlock != null)
					{
						startPrices[ResourceType.Fuel] =
							startBlock["FuelStartPrice"]?.Value<float>() ?? 0f;

						startPrices[ResourceType.Coal] =
							startBlock["CoalStartPrice"]?.Value<float>() ?? 0f;

						startPrices[ResourceType.ElectricCharge] =
							startBlock["PowerStartPrice"]?.Value<float>() ?? 0f;
					}

					for (int i = 1; i < arr.Count; i++)
					{
						var entry = arr[i];
						var jobId = entry["JobId"]?.Value<string>() ?? "UNKNOWN";

						cargoEvents.Add(new CargoEvent
						{
							JobId = jobId,
							Resource = entry["Resource"]?.Value<string>() ?? "UNKNOWN",
							ConditionPercent = entry["Condition"]?.Value<float>() ?? 100f,
							ImpactPercent = entry["Impact"]?.Value<float>() ?? 0f,
							LifetimeDays = entry["Lifetime"]?.Value<float>() / 24f ?? 1f
						});

						processedJobEvents.Add(jobId);
					}

					if (Settings.EnableDebugLogs)
					{
						Debug.Log("[DynamicResourcePrices] Loaded cargo events: " + cargoEvents.Count);
					}
				}
			}
		}
		
		private JArray CreateEventsArray()
		{
			var arr = new JArray();

			// ------------------------------
			// START PRICES BLOCK
			// ------------------------------
			float GetStart(ResourceType t)
			{
				return startPrices.TryGetValue(t, out var value) ? value : 0f;
			}

			arr.Add(new JObject
			{
				["FuelStartPrice"] = GetStart(ResourceType.Fuel),
				["CoalStartPrice"] = GetStart(ResourceType.Coal),
				["PowerStartPrice"] = GetStart(ResourceType.ElectricCharge)
			});

			// ------------------------------
			// EVENTS
			// ------------------------------
			foreach (var ev in cargoEvents)
			{
				arr.Add(new JObject
				{
					["JobId"] = ev.JobId,
					["Resource"] = ev.Resource,
					["Condition"] = ev.ConditionPercent,
					["Impact"] = ev.ImpactPercent,
					["Lifetime"] = ev.LifetimeDays * 24f
				});
			}

			return arr;
		}
		
		public void ResetEconomy()
		{
			HardResetState();

			currentPrices.Clear();
			currentTrends.Clear();

			HasSaveData = false;
			hasUsedSaveTrend = false;

			InitBasePrices();
			GenerateDailyTrends();

			if (Settings.EnableDebugLogs)
			{
				Debug.Log("[DynamicResourcePrices] ECONOMY RESET (new game)");
			}
		}
		
		public void HardResetState()
		{
			cargoEvents.Clear();
			processedJobEvents.Clear();
			var processedKeys = processedJobEvents.ToHashSet();
			foreach (var key in processedKeys)
			{
				jobEventBuffer.Remove(key);
			}
			carIdToJobId.Clear();
			startPrices.Clear();

			if (Settings.EnableDebugLogs)
			{
				Debug.Log("[DynamicResourcePrices] HARD RESET → runtime state cleared");
			}
		}
		
		public void InitializeTime(DateTime time)
		{
			lastDay = time.Day;

			float hour = time.Hour + time.Minute / 60f;

			hoursPerInterval = 24f / Settings.DailyUpdateInterval;

			lastIntervalIndex = Mathf.FloorToInt(hour / hoursPerInterval);

			if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
			{
				Debug.Log($"[DynamicResourcePrices] Init Time → Day: {lastDay}, Interval: {lastIntervalIndex}");
			}
		}
		
		public void Evaluate(DateTime currentTime)
		{
			float hour = currentTime.Hour + currentTime.Minute / 60f;
			
			if (Settings.DailyUpdateInterval != lastKnownInterval)
			{
				lastKnownInterval = Settings.DailyUpdateInterval;

				RecalculateInterval();

				InitializeTime(currentTime);
				lastIntervalIndex = -1;

				if (Settings.EnableDebugLogs)
				{
					Debug.Log("[DynamicResourcePrices] Settings changed → Reinitialized timing");
				}
			}

			if (currentTime.Day != lastDay)
			{
				lastDay = currentTime.Day;
				
				UpdateEventLifetimesPerDay();

				if (hasUsedSaveTrend)
				{
					if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
					{
						Debug.Log("[DynamicResourcePrices] New Day -> FIRST DAY AFTER LOAD (keep save trend)");
					}
					hasUsedSaveTrend = false;
				}
				else
				{
					GenerateDailyTrends();					
					if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
					{
						Debug.Log("[DynamicResourcePrices] New Day -> Trends regenerated");
					}
				}

				ApplyIntervalUpdate();
				lastIntervalIndex = 0;
			}

			int currentInterval = Mathf.FloorToInt(hour / hoursPerInterval);

			if (lastIntervalIndex == -1)
			{
				lastIntervalIndex = currentInterval;
				return;
			}

			while (lastIntervalIndex < currentInterval)
			{
				lastIntervalIndex++;
				ApplyIntervalUpdate();
				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.Log($"[DynamicResourcePrices] Interval Update → {lastIntervalIndex}");
				}
			}
		}

		private void LoadResource(JObject root, string key, ResourceType type)
		{
			if (!root.TryGetValue(key, out JToken? token))
				return;

			var obj = token as JObject;
			if (obj == null)
				return;

			float trend = obj["Trend"] != null ? obj["Trend"]!.Value<float>() / 100f : 0f;
			float price = obj["Price"] != null ? obj["Price"]!.Value<float>() : 0f;

			currentTrends[type] = trend;
			currentPrices[type] = price;
		}
		
		private void HandleHourTick(DateTime currentTime)
		{
			float hour = currentTime.Hour + currentTime.Minute / 60f;

			if (currentTime.Day != lastDay)
			{
				lastDay = currentTime.Day;
				GenerateDailyTrends();

				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.Log("[DynamicResourcePrices] New Day -> Trends regenerated");
				}

				ApplyIntervalUpdate();
				lastIntervalIndex = 0;

				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.Log("[DynamicResourcePrices] New Day → First interval applied");
				}
			}

			int currentInterval = Mathf.FloorToInt(hour / hoursPerInterval);

			if (lastIntervalIndex == -1)
			{
				lastIntervalIndex = currentInterval - 1;
			}

			if (lastIntervalIndex == -1)
			{
				lastIntervalIndex = currentInterval;
				return;
			}

			while (lastIntervalIndex < currentInterval)
			{
				lastIntervalIndex++;
				ApplyIntervalUpdate();
				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.Log($"[DynamicResourcePrices] Interval Update → {lastIntervalIndex}");
				}
			}
		}
		
		public class DynamicResourcePricesWatcher : MonoBehaviour
		{
			private WorldClockController? clock;

			void Start()
			{
				StartCoroutine(WaitForWorld());
			}

			private IEnumerator WaitForWorld()
			{
				while (!WorldStreamingInit.IsStreamingDone)
					yield return null;

				while (clock == null)
				{
					clock = UnityEngine.Object.FindObjectOfType<WorldClockController>();
					yield return null;
				}

				clock.TimeChanged += OnTimeChanged;

				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.Log("[DynamicResourcePrices] Clock ready");
				}
			}

			private void OnTimeChanged(float h, float m, DateTime time)
			{
				var inst = DynamicResourcePricesMain.Instance;

				if (inst == null)
					return;

				if (!inst.IsInitialized())
				{
					inst.FinalizeInitialization(time);
					return;
				}

				inst.Evaluate(time);
			}

			void OnDestroy()
			{
				if (clock != null)
					clock.TimeChanged -= OnTimeChanged;
			}
		}
		
		public void FinalizeInitialization(DateTime time)
		{
			if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
			{
				Debug.Log($"[DynamicResourcePrices] INIT CHECK → waitingForSaveLoad={waitingForSaveLoad}, hasSave={HasSaveData}");
			}
			
			if (isInitialized)
			{
				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.Log("[DynamicResourcePrices] INIT SKIPPED (already initialized)");
				}
				return;
			}

			if (waitingForSaveLoad)
			{
				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.Log("[DynamicResourcePrices] INIT DELAYED (waiting for save load)");
				}
				return;
			}

			if (hoursPerInterval <= 0f)
			{
				RecalculateInterval();
			}

			if (!HasSaveData && currentTrends.Count == 0)
			{
				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.Log("[DynamicResourcePrices] Fresh session → generating trends");
				}
				ResetEconomy();
			}
			else
			{
				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.Log("[DynamicResourcePrices] Using SAVE DATA (no regen)");
				}
			}

			InitializeTime(time);
			isInitialized = true;

			if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
			{
				Debug.Log("[DynamicResourcePrices] INIT COMPLETE");
			}
		}
		
		public bool IsInitialized()
		{
			return isInitialized;
		}
		
		private void RecalculateInterval()
		{
			if (Settings.DailyUpdateInterval <= 0)
			{
				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.LogWarning("[DynamicResourcePrices] Invalid interval → fallback to 24");
				}
				Settings.DailyUpdateInterval = 24;
			}

			hoursPerInterval = 24f / Settings.DailyUpdateInterval;

			if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
			{
				Debug.Log($"[DynamicResourcePrices] Interval recalculated → {Settings.DailyUpdateInterval} per day = every {hoursPerInterval}h");
			}
		}

        private void InitBasePrices()
        {
            basePrices[ResourceType.Fuel] = 3.0f;
            basePrices[ResourceType.Coal] = 2.0f;
            basePrices[ResourceType.ElectricCharge] = 1.5f;

            foreach (var kvp in basePrices)
            {
                currentPrices[kvp.Key] = kvp.Value;
            }
        }

        private void GenerateDailyTrends()
		{
			currentTrends[ResourceType.Fuel] = GetRandomTrend(Settings.DailyTrendUpdates.MaxFuelMultiplier);
			currentTrends[ResourceType.Coal] = GetRandomTrend(Settings.DailyTrendUpdates.MaxCoalMultiplier);
			currentTrends[ResourceType.ElectricCharge] = GetRandomTrend(Settings.DailyTrendUpdates.MaxPowerMultiplier);

			if (Settings.EnableDebugLogs)
			{
				Debug.Log(
					"[DynamicResourcePrices] New Daily Trends : " +
					"Fuel: " + FormatPercent(currentTrends[ResourceType.Fuel]) + ", " +
					"Coal: " + FormatPercent(currentTrends[ResourceType.Coal]) + ", " +
					"Power: " + FormatPercent(currentTrends[ResourceType.ElectricCharge])
				);
			}
		}
		
		private string FormatPercent(float value)
		{
			float percent = value * 100f;

			return percent >= 0f
				? "+" + percent.ToString("F2") + "%"
				: percent.ToString("F2") + "%";
		}

        private float GetRandomTrend(float max)
        {
            float value = UnityEngine.Random.Range(0f, max);
            return UnityEngine.Random.value > 0.5f ? value : -value;
        }

        private void ApplyIntervalUpdate()
        {			
            UpdateResource(ResourceType.Fuel, Settings.Fuel);
            UpdateResource(ResourceType.Coal, Settings.Coal);
            UpdateResource(ResourceType.ElectricCharge, Settings.Power);
			
			RefreshAllPitStops();
			
			if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
			{
				//Debug.Log("[DynamicResourcePrices] Interval Update applied");
			}
        }
		
		private void ApplyInstantEventImpact(CargoEvent ev)
		{
			float impact = ev.ImpactPercent / 100f;

			switch (ev.Resource)
			{
				case "Fuel":
					ApplyInstantPrice(ResourceType.Fuel, impact);
					break;

				case "Coal":
					ApplyInstantPrice(ResourceType.Coal, impact);
					ApplyInstantPrice(ResourceType.ElectricCharge, impact * 0.75f);
					break;
			}
		}
		
		private void ApplyInstantPrice(ResourceType type, float impact)
		{
			if (!currentPrices.ContainsKey(type))
				return;

			float oldPrice = currentPrices[type];
			float newPrice = oldPrice * (1f + impact);

			var config = GetConfig(type);
			float minCap = Mathf.Max(config.MinPriceCap, 0.25f);
			newPrice = Mathf.Clamp(newPrice, minCap, config.MaxPriceCap);

			currentPrices[type] = newPrice;

			if (Settings.EnableDebugLogs)
			{
				//Debug.Log($"[DynamicResourcePrices] INSTANT EVENT IMPACT → {type} {FormatImpact(impact * 100f)} | {oldPrice:F2}$ → {newPrice:F2}$");
			}
		}
		
		private void UpdateEventLifetimesPerDay()
		{
			for (int i = cargoEvents.Count - 1; i >= 0; i--)
			{
				var ev = cargoEvents[i];

				ev.LifetimeDays -= 1f;

				if (ev.LifetimeDays <= 0f)
				{
					if (Settings.EnableDebugLogs)
					{
						Debug.Log($"[DynamicResourcePrices] EVENT EXPIRED → {ev.JobId}");
					}

					cargoEvents.RemoveAt(i);
					processedJobEvents.Remove(ev.JobId);
				}
			}

			CheckForEventReset(ResourceType.Fuel, "Fuel");
			CheckForEventReset(ResourceType.Coal, "Coal");
			CheckForEventReset(ResourceType.ElectricCharge, "Power");
		}
		
		private void CheckForEventReset(ResourceType type, string resourceName)
		{
			bool hasActiveEvents = cargoEvents.Any(e => 
				e.Resource == resourceName && e.LifetimeDays > 0f
			);

			if (!hasActiveEvents && startPrices.ContainsKey(type))
			{
				float oldPrice = currentPrices[type];
				float resetPrice = startPrices[type];

				currentPrices[type] = resetPrice;

				startPrices.Remove(type);

				if (Settings.EnableDebugLogs)
				{
					Debug.Log(
						$"[DynamicResourcePrices] EVENT RESET → {type} | {oldPrice:F2}$ → {resetPrice:F2}$"
					);
				}
			}
		}

        private void UpdateResource(ResourceType type, ResourceSettings config)
		{
			if (!currentPrices.ContainsKey(type) || !currentTrends.ContainsKey(type))
			{
				if (Settings.EnableDebugLogs)
				{
					Debug.LogWarning("[DynamicResourcePrices] Missing data for " + type);
				}
				return;
			}

			float oldPrice = currentPrices[type];
			float trend = currentTrends[type];

			float intervalFactor = 1f / Settings.DailyUpdateInterval;

			float trendComponent = trend * intervalFactor;
			float randomComponent = UnityEngine.Random.Range(-config.MaxPriceMultiplier, config.MaxPriceMultiplier) * intervalFactor;

			float eventComponent = GetActiveEventImpact(type);
			float changePercent = trendComponent + eventComponent + randomComponent;

			float newPrice = oldPrice * (1f + changePercent);
			newPrice = Mathf.Clamp(newPrice, config.MinPriceCap, config.MaxPriceCap);

			currentPrices[type] = newPrice;

			// ---------- FORMAT ----------
			string name = GetResourceName(type);
			string padding = GetPadding(name);

			string arrow = trend >= 0f ? "↑" : "↓";

			string percentString = changePercent >= 0f
				? "+" + (changePercent * 100f).ToString("F2")
				: (changePercent * 100f).ToString("F2");

			if (Settings.EnableDebugLogs)
			{
				Debug.Log(
					"[DynamicResourcePrices] " + name + " prices changed!" +
					padding +
					"Trend: " + FormatPercent(trendComponent) + " " +
					"Event: " + FormatPercent(eventComponent) + " " +
					"Random: " + FormatPercent(randomComponent) + " " +
					"Total: " + FormatPercent(changePercent) + " " +
					"Old: " + oldPrice.ToString("F2") + "$ " +
					"New: " + newPrice.ToString("F2") + "$"
				);
			}
		}
		
		private string GetResourceName(ResourceType type)
		{
			switch (type)
			{
				case ResourceType.ElectricCharge: return "Power";
				case ResourceType.Fuel: return "Fuel";
				case ResourceType.Coal: return "Coal";
				default: return type.ToString();
			}
		}
		
		private ResourceType MapResource(string resource)
		{
			if (string.IsNullOrEmpty(resource))
				return ResourceType.ElectricCharge;

			switch (resource)
			{
				case "Fuel": return ResourceType.Fuel;
				case "Coal": return ResourceType.Coal;
				default: return ResourceType.ElectricCharge;
			}
		}
		
		private string GetPadding(string name)
		{
			// sorgt dafür, dass alles untereinander aligned ist
			int targetLength = 6; // längstes Wort = "Power" (5) + etwas Luft

			int spaces = targetLength - name.Length;

			if (spaces < 1) spaces = 1;

			return new string(' ', spaces);
		}

        public float GetPrice(ResourceType type)
        {
            if (!currentPrices.ContainsKey(type))
                return -1f;

            return currentPrices[type];
        }

        public void OnNewDay()
        {
            GenerateDailyTrends();
        }
		
		private void RefreshAllPitStops()
		{
			try
			{
				var pstType = AccessTools.TypeByName("PitStopStation");
				if (pstType == null) return;

				var stations = UnityEngine.Object.FindObjectsOfType(pstType);

				foreach (var st in stations)
				{
					if (st == null) continue; // NEW

					var pitstopField = AccessTools.Field(pstType, "pitstop");
					if (pitstopField == null) continue; // NEW

					var pitstop = pitstopField.GetValue(st);
					if (pitstop == null) continue;

					// --- SAFE IsInPit ---
					bool isInPit = false;

					try
					{
						var isInPitMethod = AccessTools.Method(pitstop.GetType(), "IsCarInPitStop");
						if (isInPitMethod != null)
						{
							isInPit = (bool)isInPitMethod.Invoke(pitstop, null);
						}
					}
					catch
					{
						continue; // NEW: skip broken state
					}

					if (!isInPit) continue;

					// --- SAFE SelectedCar ---
					object? selectedCar = null;

					try
					{
						var prop = AccessTools.Property(pitstop.GetType(), "SelectedCar");
						if (prop != null)
						{
							selectedCar = prop.GetValue(pitstop);
						}
					}
					catch
					{
						continue; // NEW
					}

					if (selectedCar == null) continue;

					// --- SAFE Indicators ---
					var indicatorsField = AccessTools.Field(pstType, "locoResourceModules");
					if (indicatorsField == null) continue;

					var indicators = indicatorsField.GetValue(st);
					if (indicators == null) continue;

					var indType = indicators.GetType();

					var updateIndep = AccessTools.Method(indType, "UpdateIndependentPrices");
					var updateType = AccessTools.Method(indType, "UpdatePricesDependingOnLocoType");

					object? livery = null;

					try
					{
						var liveryProp = selectedCar.GetType().GetProperty("carLivery");
						if (liveryProp != null)
							livery = liveryProp.GetValue(selectedCar);
					}
					catch
					{
						// ignore
					}

					// --- APPLY ---
					try
					{
						updateIndep?.Invoke(indicators, new object[] { selectedCar });

						if (updateType != null && livery != null)
						{
							updateType.Invoke(indicators, new object[] { selectedCar, livery });
						}
					}
					catch
					{
						continue; // NEW
					}

					// --- DISPLAY REPORT (SAFE!) ---
					try
					{
						var dispLatest = AccessTools.Method(pstType, "DisplayLatestCarParamsReport");

						if (dispLatest != null)
						{
							dispLatest.Invoke(st, new object[] { false });
						}
					}
					catch
					{
						// CRITICAL FIX: no crash here anymore
						continue;
					}
				}

				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.Log("[DynamicResourcePrices] PitStop UI refreshed");
				}
			}
			catch (Exception ex)
			{
				if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
				{
					Debug.LogError("[DynamicResourcePrices] RefreshAllPitStops error: " + ex);
				}
			}
		}
		
		public void ForceRefreshPitStops()
		{
			RefreshAllPitStops();
			if (DynamicResourcePricesMain.Settings.EnableDebugLogs)
			{
				Debug.Log("[DynamicResourcePrices] Manual PitStop refresh triggered");
			}
		}
		
		private void ApplyCargoImpact(CargoEvent ev)
		{
			float damageFactor = 1f - (ev.ConditionPercent / 100f);

			if (damageFactor <= 0f)
				return;

			switch (ev.Resource)
			{
				case "Fuel":
					IncreaseTrend(ResourceType.Fuel, damageFactor);
					break;

				case "Coal":
					IncreaseTrend(ResourceType.Coal, damageFactor);
					IncreaseTrend(ResourceType.ElectricCharge, damageFactor * 0.75f);
					break;
			}
		}
		
		private void IncreaseTrend(ResourceType type, float amount)
		{
			if (!currentTrends.ContainsKey(type))
				return;

			currentTrends[type] += amount;

			currentTrends[type] = Mathf.Clamp(currentTrends[type], -1f, 1f);

			if (Settings.EnableDebugLogs)
			{
				Debug.Log($"[DynamicResourcePrices] MARKET IMPACT → {type} {FormatImpact(amount * 100f)}");
			}
		}
		
		private string FormatImpact(float value)
		{
			string arrow = value >= 0f ? "↑" : "↓";

			string percent = value >= 0f
				? "+" + value.ToString("F2")
				: value.ToString("F2");

			return $"{arrow} {percent}%";
		}
		
		public void RegisterCargoEvent(string jobId, string resource, float percent)
		{
			if (jobId == "UNKNOWN")
				return;

			if (!jobEventBuffer.TryGetValue(jobId, out var ev))
			{
				ev = new AggregatedCargoEvent
				{
					JobId = jobId,
					Resource = resource
				};

				jobEventBuffer[jobId] = ev;
			}

			ev.WagonPercents.Add(percent);

			if (Settings.EnableDebugLogs)
			{
				//Debug.Log($"[DynamicResourcePrices] BUFFER ADD → {jobId} | {percent:F1}%");
			}
		}
		
		private float GetActiveEventImpact(ResourceType type)
		{
			float totalImpact = 0f;

			foreach (var ev in cargoEvents)
			{
				if (ev.LifetimeDays <= 0f)
					continue;

				float impact = ev.ImpactPercent / 100f;

				// NEW: exponentielle Normalisierung pro Intervall
				float intervalImpact = Mathf.Pow(1f + impact, 1f / Settings.DailyUpdateInterval) - 1f;

				switch (ev.Resource)
				{
					case "Fuel":
						if (type == ResourceType.Fuel)
							totalImpact += intervalImpact;
						break;

					case "Coal":
						if (type == ResourceType.Coal)
							totalImpact += intervalImpact;

						if (type == ResourceType.ElectricCharge)
							totalImpact += intervalImpact * 0.75f;
						break;
				}

				// DEBUG (optional)
				if (Settings.EnableDebugLogs)
				{
					//Debug.Log($"[DynamicResourcePrices] EVENT NORMALIZED ? {type} | raw: {impact * 100f:F2}% | per interval: {intervalImpact * 100f:F4}%");
				}
			}

			return totalImpact;
		}
		
		private float CalculateImpactPerWagon(float conditionPercent)
		{
			float condition = Mathf.Clamp(conditionPercent, 0f, 100f);

			float t = 1f - (condition / 100f);
			// 0% damage → t = 0
			// 100% damage → t = 1

			float reward = Settings.EventRewardPercent / 100f;
			float penalty = Settings.EventPenaltyPercent / 100f;

			// IMPORTANT:
			// reward is applied ONLY if perfect (t = 0)
			// penalty scales with damage

			float result = Mathf.Lerp(-reward, penalty, t);

			return result;
		}
		
		private void ApplyImpactToMarket(CargoEvent ev, float impact)
		{
			switch (ev.Resource)
			{
				case "Fuel":
					IncreaseTrend(ResourceType.Fuel, impact);
					break;

				case "Coal":
					IncreaseTrend(ResourceType.Coal, impact);
					IncreaseTrend(ResourceType.ElectricCharge, impact * 0.75f);
					break;
			}
		}
		
		public void CacheCargoHealth(Car car, float percent)
		{
			cachedCargoHealth[car] = percent;
		}
		
		public void RegisterCarToJob(string carId, string jobId)
		{
			if (string.IsNullOrEmpty(carId) || string.IsNullOrEmpty(jobId))
				return;

			carIdToJobId[carId] = jobId;

			if (Settings.EnableDebugLogs)
			{
				//Debug.Log($"[DynamicResourcePrices] MAP → {carId} → {jobId}");
			}
		}

		public float GetCachedCargoHealth(Car car)
		{
			if (cachedCargoHealth.TryGetValue(car, out var value))
				return value;

			return 100f;
		}
		
		private void FinalizeSingleJob(string jobId)
		{
			if (!jobEventBuffer.TryGetValue(jobId, out var jobEvent))
				return;

			if (processedJobEvents.Contains(jobId))
				return;

			int wagonCount = jobEvent.WagonPercents.Count;
			if (wagonCount == 0)
				return;

			bool allPerfect = jobEvent.WagonPercents.All(p => p >= 99.9f);

			float maxReward = Settings.EventRewardPercent / 100f;
			float maxPenalty = Settings.EventPenaltyPercent / 100f;

			float totalImpact = 0f;

			if (allPerfect)
			{
				totalImpact = -maxReward;
			}
			else
			{
				float rewardPool = maxReward * 0.5f;
				float rewardPerWagon = rewardPool / wagonCount;

				foreach (var percent in jobEvent.WagonPercents)
				{
					float condition = Mathf.Clamp(percent, 0f, 100f);

					if (condition >= 99.9f)
					{
						totalImpact -= rewardPerWagon;
						continue;
					}

					float t = condition >= 50f
						? (100f - condition) / 50f
						: 1f;

					float penalty = t * maxPenalty;
					totalImpact += penalty;
				}
			}

			float maxTotalPenalty = maxPenalty * wagonCount;
			totalImpact = Mathf.Clamp(totalImpact, -maxReward, maxTotalPenalty);

			bool isPositive = totalImpact < 0f;

			float baseLifetime = isPositive
				? Settings.RewardLifetimeDays
				: Settings.PenaltyLifetimeDays;

			float lifetimeDays = Settings.LifetimeRandom
				? UnityEngine.Random.Range(1f, baseLifetime)
				: baseLifetime;

			var finalEvent = new CargoEvent
			{
				JobId = jobId,
				Resource = jobEvent.Resource,
				ConditionPercent = jobEvent.WagonPercents.Average(),
				ImpactPercent = totalImpact * 100f,
				LifetimeDays = lifetimeDays
			};

			ResourceType type = MapResource(jobEvent.Resource);

			// ?? CORRECT START PRICE LOGIC
			bool hasActiveEventsForResource = cargoEvents.Any(e =>
				e.Resource == jobEvent.Resource && e.LifetimeDays > 0f
			);

			bool hasValidStartPrice =
				startPrices.ContainsKey(type) && startPrices[type] > 0f;

			if (!hasActiveEventsForResource && !hasValidStartPrice)
			{
				startPrices[type] = currentPrices[type];

				if (Settings.EnableDebugLogs)
				{
					Debug.Log($"[DynamicResourcePrices] START PRICE SET : {type} | {currentPrices[type]:F2}$");
				}
			}

			ApplyInstantEventImpact(finalEvent);

			cargoEvents.Add(finalEvent);
			processedJobEvents.Add(jobId);

			jobEventBuffer.Remove(jobId);

			if (Settings.EnableDebugLogs)
			{
				Debug.Log(
					$"[DynamicResourcePrices] FINAL EVENT : {jobId} | {jobEvent.Resource} | " +
					$"Wagons: {wagonCount} | Avg: {finalEvent.ConditionPercent:F1}% | " +
					$"Impact: {FormatImpact(finalEvent.ImpactPercent)}"
				);
			}
		}
		
		public void ResolveFinalCargoEvents()
		{
			var controllers = UnityEngine.Object.FindObjectsOfType<CarDebtController>();

			foreach (var controller in controllers)
			{
				var tracker = controller.CarDebtTracker;
				if (tracker == null) continue;

				var debtDataField = AccessTools.Field(tracker.GetType(), "debtData");
				var debtData = debtDataField?.GetValue(tracker);
				if (debtData == null) continue;

				var idField = AccessTools.Field(debtData.GetType(), "id");
				string carId = idField?.GetValue(debtData) as string ?? "UNKNOWN";

				if (!carIdToJobId.TryGetValue(carId, out var jobId))
					continue;

				var saveMethod = AccessTools.Method(tracker.GetType(), "GetDebtTrackerCarSaveData");
				var saveData = saveMethod?.Invoke(tracker, null) as JObject;

				if (saveData == null)
					continue;

				bool unloaded = saveData["cargoUnloaded"]?.Value<bool>() ?? false;
				if (!unloaded)
					continue;

				float percent = saveData["cargoEndV"]?.Value<float>() ?? 100f;
				percent = Mathf.Clamp(percent, 0f, 100f);

				RegisterCargoEvent(jobId, "Fuel", percent);

				if (Settings.EnableDebugLogs)
				{
					Debug.Log($"[DynamicResourcePrices] CARGO UNLOADED : {jobId} | {carId} | {percent:F1}%");
				}
			}

			carIdToJobId.Clear();

			// 🔥 NEU → JOBS SEPARAT FINALISIEREN
			var jobIds = jobEventBuffer.Keys.ToList();

			foreach (var jobId in jobIds)
			{
				FinalizeSingleJob(jobId);
			}
		}
		
		private ResourceSettings GetConfig(ResourceType type)
		{
			switch (type)
			{
				case ResourceType.Fuel: return Settings.Fuel;
				case ResourceType.Coal: return Settings.Coal;
				case ResourceType.ElectricCharge: return Settings.Power;
				default: throw new Exception("Unknown resource");
			}
		}
		
		public IEnumerator DelayedFinalize(DynamicResourcePricesMain inst)
		{
			yield return new WaitForSeconds(0.5f);

			inst.ResolveFinalCargoEvents();
		}
		
		[Serializable]
		public class CargoEvent
		{
			public string JobId = "UNKNOWN";
			public string Resource = "UNKNOWN";
			public float ConditionPercent;
			public float ImpactPercent;
			public float LifetimeDays;
		}
		
		[Serializable]
		public class AggregatedCargoEvent
		{
			public string JobId = "UNKNOWN";
			public string Resource = "UNKNOWN";
 
			public List<float> WagonPercents = new List<float>();
		}
		
		// =========================================================
		// DEBUG: END ALL EVENTS
		// =========================================================
		public void Debug_EndAllEvents()
		{
			foreach (var type in new[] 
			{ 
				ResourceType.Fuel, 
				ResourceType.Coal, 
				ResourceType.ElectricCharge 
			})
			{
				if (startPrices.ContainsKey(type) && startPrices[type] > 0f)
				{
					float oldPrice = currentPrices[type];
					float resetPrice = startPrices[type];

					currentPrices[type] = resetPrice;

					if (Settings.EnableDebugLogs)
					{
						Debug.Log($"[DynamicResourcePrices] DEBUG RESET : {type} | {oldPrice:F2}$ → {resetPrice:F2}$");
					}
				}
			}

			// ?? CLEAR EVERYTHING
			cargoEvents.Clear();
			processedJobEvents.Clear();
			jobEventBuffer.Clear();
			startPrices.Clear();

			if (Settings.EnableDebugLogs)
			{
				Debug.Log("[DynamicResourcePrices] DEBUG : ALL EVENTS CLEARED");
			}

			ForceRefreshPitStops();
		}
		
		// =========================================================
		// DEBUG: RESET ECONOMY (FULL)
		// =========================================================
		public void Debug_ResetEconomy()
		{
			// ?? FULL RESET
			HardResetState();

			currentPrices.Clear();
			currentTrends.Clear();

			HasSaveData = false;
			hasUsedSaveTrend = false;

			InitBasePrices();
			GenerateDailyTrends();

			if (Settings.EnableDebugLogs)
			{
				Debug.Log("[DynamicResourcePrices] DEBUG : ECONOMY FULL RESET");
			}

			ForceRefreshPitStops();
		}
    }
}