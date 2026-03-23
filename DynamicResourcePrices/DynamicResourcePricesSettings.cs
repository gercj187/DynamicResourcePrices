using System;
using System.Globalization;
using UnityEngine;
using UnityModManagerNet;

namespace DynamicResourcePrices
{
    [Serializable]
    public class DynamicResourcePricesSettings : UnityModManager.ModSettings
    {
        public int DailyUpdateInterval = 4;

        public DailyTrendSettings DailyTrendUpdates = new DailyTrendSettings();

        public ResourceSettings Power = new ResourceSettings();
        public ResourceSettings Fuel = new ResourceSettings();
        public ResourceSettings Coal = new ResourceSettings();

        public bool EnableDebugLogs = false;
		
		// ---------------- EVENT SETTINGS ----------------

		public float EventRewardPercent = 2.0f;   // max reward per job (all wagons 100%)
		public float EventPenaltyPercent = 3.0f;  // max penalty per wagon (0% condition)

		public float RewardLifetimeDays = 2f;
		public float PenaltyLifetimeDays = 3f;

		public bool LifetimeRandom = false;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public void Draw()
		{
			// =========================================================
			// ---------------- DAILY TRENDS ----------------
			// =========================================================
			GUILayout.Space(10);

			GUILayout.BeginVertical("box");
			GUILayout.Space(5);

			DrawIndentedLabel("< Daily Trends >", 300);

			GUILayout.Space(5);

			DrawTrendRow("Coal:", ref DailyTrendUpdates.MaxCoalMultiplier);
			DrawTrendRow("Fuel:", ref DailyTrendUpdates.MaxFuelMultiplier);
			DrawTrendRow("Power:", ref DailyTrendUpdates.MaxPowerMultiplier);
			GUILayout.Space(5);
			
			DrawIndentedLabel("The Daily Trend sets the overall price direction for each in-game day,", 150);
			DrawIndentedLabel("causing resources to increase or decrease within a configurable range.", 150);
			DrawIndentedLabel("It influences long-term price changes and creates a dynamic economy.", 150);
			
			GUILayout.Space(5);
			GUILayout.EndVertical();

			// =========================================================
			// ---------------- INTERVAL ----------------
			// =========================================================
			GUILayout.Space(10);

			GUILayout.BeginVertical("box");
			GUILayout.Space(5);

			DrawIndentedLabel("< Update Interval per Day >", 300);

			GUILayout.Space(5);

			GUILayout.BeginHorizontal();
			GUILayout.Label("Updates:", GUILayout.Width(150));

			DailyUpdateInterval = Mathf.RoundToInt(
				GUILayout.HorizontalSlider(DailyUpdateInterval, 3, 24, GUILayout.Width(500))
			);

			GUILayout.Label(DailyUpdateInterval.ToString(), GUILayout.Width(50));
			GUILayout.EndHorizontal();

			GUILayout.Space(5);

			DrawIndentedLabel(GetIntervalDescription(DailyUpdateInterval), 300);

			GUILayout.Space(5);
			GUILayout.EndVertical();

			// =========================================================
			// ---------------- RESOURCE SETTINGS ----------------
			// =========================================================
			GUILayout.Space(10);

			GUILayout.BeginVertical("box");
			GUILayout.Space(5);

			DrawIndentedLabel("< Resource Settings >", 300);

			GUILayout.Space(5);

			DrawResourceSection("Coal", Coal);
			GUILayout.Space(5);

			DrawResourceSection("Fuel", Fuel);
			GUILayout.Space(5);

			DrawResourceSection("Power", Power);

			GUILayout.Space(5);
			GUILayout.EndVertical();

			// =========================================================
			// ---------------- EVENT SETTINGS ----------------
			// =========================================================
			GUILayout.Space(10);

			GUILayout.BeginVertical("box");
			GUILayout.Space(5);

			DrawIndentedLabel("< Event Settings >", 300);

			GUILayout.Space(5);

			// --- Reward ---
			GUILayout.BeginHorizontal();
			GUILayout.Label("Event Reward (% per Job):", GUILayout.Width(250));

			EventRewardPercent =
				GUILayout.HorizontalSlider(EventRewardPercent, 0f, 5f, GUILayout.Width(400));

			GUILayout.Label(EventRewardPercent.ToString("F2") + "%", GUILayout.Width(60));
			GUILayout.EndHorizontal();

			// --- Penalty ---
			GUILayout.BeginHorizontal();
			GUILayout.Label("Event Penalty (% per Wagon):", GUILayout.Width(250));

			EventPenaltyPercent =
				GUILayout.HorizontalSlider(EventPenaltyPercent, 0f, 5f, GUILayout.Width(400));

			GUILayout.Label(EventPenaltyPercent.ToString("F2") + "%", GUILayout.Width(60));
			GUILayout.EndHorizontal();

			GUILayout.Space(5);

			// --- Lifetime Reward ---
			GUILayout.BeginHorizontal();
			GUILayout.Label("Reward Lifetime (days):", GUILayout.Width(250));
			RewardLifetimeDays = ParseFloatField(RewardLifetimeDays, 100);
			GUILayout.EndHorizontal();

			// --- Lifetime Penalty ---
			GUILayout.BeginHorizontal();
			GUILayout.Label("Penalty Lifetime (days):", GUILayout.Width(250));
			PenaltyLifetimeDays = ParseFloatField(PenaltyLifetimeDays, 100);
			GUILayout.EndHorizontal();

			GUILayout.Space(5);

			// --- Random Toggle ---
			LifetimeRandom = GUILayout.Toggle(LifetimeRandom, "Random Lifetime (1 → Max)");

			GUILayout.Space(5);

			// --- Description ---
			DrawIndentedLabel("Reward applies when all wagons are delivered at 100% condition.", 150);
			DrawIndentedLabel("Penalty scales per wagon depending on damage.", 150);

			GUILayout.Space(5);
			GUILayout.EndVertical();

			// =========================================================
			// ---------------- DEBUG ----------------
			// =========================================================
			GUILayout.Space(10);

			//EnableDebugLogs = GUILayout.Toggle(EnableDebugLogs, "Enable Debug");

			// =========================================================
			// ---------------- BUTTONS ----------------
			// =========================================================
			if (EnableDebugLogs)
			{
				GUILayout.Space(10);

				GUILayout.BeginHorizontal();

				if (GUILayout.Button("Reset Settings", GUILayout.Width(150)))
				{
					ResetToDefault();
				}	
				
				if (GUILayout.Button("End all Events", GUILayout.Width(150)))
				{
					DynamicResourcePricesMain.Instance?.Debug_EndAllEvents();
				}

				if (GUILayout.Button("Reset Economy", GUILayout.Width(150)))
				{
					DynamicResourcePricesMain.Instance?.Debug_ResetEconomy();
				}

				GUILayout.EndHorizontal();	
			}
		}

        // ---------------- HELPERS ----------------
		
		private void DrawIndentedLabel(string text, float leftSpace)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Space(leftSpace);
			GUILayout.Label(text);
			GUILayout.EndHorizontal();
		}
		
		private void DrawTrendRow(string label, ref float value)
		{
			GUILayout.BeginHorizontal();

			GUILayout.Label(label, GUILayout.Width(120));

			GUILayout.Label("0.00", GUILayout.Width(40));

			value = GUILayout.HorizontalSlider(value, 0.01f, 0.50f, GUILayout.Width(420));

			GUILayout.Label(value.ToString("F2"), GUILayout.Width(50));

			GUILayout.EndHorizontal();
		}
		
		private void DrawResourceSection(string name, ResourceSettings res)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label($"{name} Min Price:", GUILayout.Width(200));
			res.MinPriceCap = ParseFloatField(res.MinPriceCap, 100);
			GUILayout.EndHorizontal();

			GUILayout.BeginHorizontal();
			GUILayout.Label($"{name} Max Price:", GUILayout.Width(200));
			res.MaxPriceCap = ParseFloatField(res.MaxPriceCap, 100);
			GUILayout.EndHorizontal();

			res.MinPriceCap = Mathf.Clamp(res.MinPriceCap, 0.25f, 100f);
			res.MaxPriceCap = Mathf.Clamp(res.MaxPriceCap, 0, 100);

			if (res.MinPriceCap > res.MaxPriceCap)
				res.MinPriceCap = res.MaxPriceCap;

			GUILayout.BeginHorizontal();

			GUILayout.Label($"{name} Volatility:", GUILayout.Width(200));

			res.MaxPriceMultiplier =
				GUILayout.HorizontalSlider(res.MaxPriceMultiplier, 0.01f, 1.00f, GUILayout.Width(500));

			GUILayout.Label(res.MaxPriceMultiplier.ToString("F2"), GUILayout.Width(50));

			GUILayout.EndHorizontal();
		}
		
		private float ParseFloatField(float value, float width)
		{
			string text = GUILayout.TextField(value.ToString("F2", CultureInfo.InvariantCulture), GUILayout.Width(width));

			if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
				return result;

			return value;
		}
		
		private string GetIntervalDescription(int interval)
		{
			float hours = 24f / interval;

			if (interval == 24)
				return "Price updates every hour";

			if (interval == 12)
				return "Price updates every 2 hours";

			if (interval == 6)
				return "Price updates every 4 hours";

			if (interval == 4)
				return "Price updates at 6, 12, 18 and 24 o'clock";

			if (interval == 3)
				return "Price updates every 8 hours";

			// fallback (für krumme Werte wie 19)
			return $"Price updates every {hours:F1} hours";
		}

        private void ResetToDefault()
        {
            DailyUpdateInterval = 4;
			
			EventRewardPercent = 2.0f;
			EventPenaltyPercent = 3.0f;

			RewardLifetimeDays = 2f;
			PenaltyLifetimeDays = 3f;

			LifetimeRandom = false;

            DailyTrendUpdates = new DailyTrendSettings();

            Power = new ResourceSettings();
            Fuel = new ResourceSettings();
            Coal = new ResourceSettings();

            EnableDebugLogs = false;

            Debug.Log("[DynamicResourcePrices] Settings reset to default");
        }
    }

    [Serializable]
    public class DailyTrendSettings
    {
        public float MaxPowerMultiplier = 0.10f;
        public float MaxFuelMultiplier = 0.10f;
        public float MaxCoalMultiplier = 0.10f;
    }

    [Serializable]
    public class ResourceSettings
    {
        public float MinPriceCap = 0.25f;
        public float MaxPriceCap = 10.00f;
        public float MaxPriceMultiplier = 0.10f;
    }
}