using System;
using System.IO;
using BepInEx.Logging;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;


namespace TabsAgentMod
{
    public class FileBridge
    {
        private readonly string _ioDirectory;
        private readonly string _statePath;
        private readonly string _planPath;
        private readonly string _resetPath;
        private readonly string _startBattlePath;
        private readonly ManualLogSource _logger;
        private DateTime _lastPlanWriteUtc = DateTime.MinValue;
        private DateTime _lastResetWriteUtc = DateTime.MinValue;
        private DateTime _lastStartBattleWriteUtc = DateTime.MinValue;

        public FileBridge(string ioDirectory, ManualLogSource logger)
        {
            _ioDirectory = ioDirectory;
            _statePath = Path.Combine(_ioDirectory, "state.json");
            _planPath = Path.Combine(_ioDirectory, "plan.json");
            _resetPath = Path.Combine(_ioDirectory, "reset.json");
            _startBattlePath = Path.Combine(_ioDirectory, "start_battle.json");
            _logger = logger;
            if (!Directory.Exists(_ioDirectory))
            {
                Directory.CreateDirectory(_ioDirectory);
                _logger.LogInfo($"Created IO directory at {_ioDirectory}");
            }
        }

        public void WriteState(StatePayload state)
        {

            var serializer = new DataContractJsonSerializer(typeof(StatePayload));
            using (var stream = new FileStream(_statePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                serializer.WriteObject(stream, state);
            }
        }

        public PlanPayload TryReadPlan()
        {
            // Read plan.json if it exists and changed since last read.
            // Return null if missing, unchanged, or invalid.
            if (!File.Exists(_planPath))
            {
                return null;
            }

            DateTime lastWriteTime = File.GetLastWriteTimeUtc(_planPath);
            if (lastWriteTime <= _lastPlanWriteUtc)
            {
                return null;
            }

            try
            {
                byte[] data = File.ReadAllBytes(_planPath);
                int offset = 0;
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                {
                    offset = 3;
                }

                var serializer = new DataContractJsonSerializer(typeof(PlanPayload));
                using (var stream = new MemoryStream(data, offset, data.Length - offset))
                {
                    PlanPayload plan = (PlanPayload)serializer.ReadObject(stream);
                    _lastPlanWriteUtc = lastWriteTime;
                    return plan;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to read plan.json: {ex.Message}");
                return null;
            }
        }

        public bool TryConsumeResetRequest()
        {
            if (!File.Exists(_resetPath))
            {
                return false;
            }

            DateTime lastWriteTime = File.GetLastWriteTimeUtc(_resetPath);
            if (lastWriteTime <= _lastResetWriteUtc)
            {
                return false;
            }

            _lastResetWriteUtc = lastWriteTime;
            return true;
        }

        public bool TryConsumeStartBattleRequest()
        {
            // Same mtime-dedup shape as TryConsumeResetRequest.
            if (!File.Exists(_startBattlePath))
            {
                return false;
            }

            DateTime lastWriteTime = File.GetLastWriteTimeUtc(_startBattlePath);
            if (lastWriteTime <= _lastStartBattleWriteUtc)
            {
                return false;
            }

            _lastStartBattleWriteUtc = lastWriteTime;
            return true;
        }
    }
}
