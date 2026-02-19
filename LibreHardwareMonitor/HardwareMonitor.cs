using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using Rainmeter;

namespace RainmeterLHM
{
    // Mirrors LHM's ISensor — one sensor reading on a piece of hardware.
    class Sensor
    {
        public string Name; // e.g. "CPU Total"
        public string Type; // e.g. "Load"
        public string Id;   // e.g. "/amdcpu/0/load/0"  — used in HTTP requests
    }

    // Mirrors LHM's IHardware — a hardware device with its own sensors and optional sub-hardware.
    class Hardware
    {
        public string Name;                                       // e.g. "AMD Ryzen 9 5900X"
        public string Type;                                       // e.g. "cpu"  (from ImageURL filename)
        public List<Sensor>  Sensors    = new List<Sensor>();
        public List<Hardware> SubHardware = new List<Hardware>();
    }

    // Manages the HTTP connection to LHM and owns the hardware/sensor tree.
    // One per skin; identified by the presence of URL= in the measure's settings.
    class ParentMeasure
    {
        // Explicit parents keyed "E:{skinPtr}::{measureName}".
        // Implicit (localhost fallback) keyed "I:{skinPtr}".
        private static readonly Dictionary<string, ParentMeasure> Instances =
            new Dictionary<string, ParentMeasure>(StringComparer.Ordinal);

        private static readonly HttpClient Http =
            new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        private static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };


        // Flat list of ALL hardware (top-level and sub-hardware) in DFS order.
        // Sub-hardware is also reachable via Hardware.SubHardware for display,
        // but the flat list is what FindSensorId iterates when matching.
        private List<Hardware> _hardware = new List<Hardware>();

        private Dictionary<string, Dictionary<string, double>> _sensorCache =
            new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        private DateTime _lastSensorRefresh = DateTime.MinValue;

        internal string Url;
        internal API Api;
        private string _instanceKey;

        private static string ExplicitKey(IntPtr skin, string name) => $"E:{skin.ToInt64()}::{name}";
        private static string ImplicitKey(IntPtr skin)               => $"I:{skin.ToInt64()}";

        internal void Reload(API rm, string url)
        {
            Api          = rm;
            Url          = url.TrimEnd('/');
            _instanceKey = ExplicitKey(rm.GetSkin(), rm.GetMeasureName());
            Instances[_instanceKey] = this;
            RefreshHardwareTree();
        }

        internal static ParentMeasure FindInSkin(IntPtr skin)
        {
            string prefix = $"E:{skin.ToInt64()}::";
            foreach (var kvp in Instances)
                if (kvp.Key.StartsWith(prefix)) return kvp.Value;
            return null;
        }

        internal static ParentMeasure FindByName(IntPtr skin, string measureName)
        {
            Instances.TryGetValue(ExplicitKey(skin, measureName), out ParentMeasure p);
            return p;
        }

        internal static ParentMeasure GetOrCreateImplicit(IntPtr skin, API api)
        {
            string key = ImplicitKey(skin);
            if (Instances.TryGetValue(key, out ParentMeasure existing)) return existing;

            var p = new ParentMeasure { Api = api, Url = "http://localhost:8085", _instanceKey = key };
            Instances[key] = p;
            p.RefreshHardwareTree();
            return p;
        }

        internal void Dispose()
        {
            if (_instanceKey != null) Instances.Remove(_instanceKey);
        }

        // Fetches data.json and rebuilds the hardware/sensor tree.
        internal void RefreshHardwareTree()
        {
            try
            {
                string dataJson = Http.GetStringAsync(Url + "/data.json").GetAwaiter().GetResult();
                var root        = Json.Deserialize<Dictionary<string, object>>(dataJson);
                var newHardware = new List<Hardware>();
                WalkNode(root, null, newHardware);
                _hardware = newHardware;

                _sensorCache = ParseSensorValuesFromJson(dataJson);
                _lastSensorRefresh = DateTime.UtcNow;

                int sensorCount = 0;
                foreach (var hw in _hardware) sensorCount += hw.Sensors.Count;
                Api.Log(API.LogType.Notice,
                    $"LibreHardwareMonitor: found {_hardware.Count} hardware, {sensorCount} sensors from {Url}");
            }
            catch (Exception ex)
            {
                Api.Log(API.LogType.Error,
                    "LibreHardwareMonitor: failed to load data.json: " + ex.Message);
            }
        }

        // Recursively walks the data.json node tree, building Hardware and Sensor objects.
        //
        // Three kinds of non-root nodes:
        //   1. Sensor node   — SensorId is non-empty → create Sensor, add to currentHardware
        //   2. Hardware node — HardwareId is non-empty → create Hardware, recurse into children
        //   3. Everything else (sensor-type groups, computer root, transparent containers)
        //                     → recurse with the same currentHardware (pass through)
        private static void WalkNode(
            Dictionary<string, object> node,
            Hardware currentHardware,
            List<Hardware> allHardware)
        {
            if (node == null) return;

            string text       = GetStr(node, "Text");
            string sensorId   = GetStr(node, "SensorId");
            string hardwareId = GetStr(node, "HardwareId");
            string type       = GetStr(node, "Type");
            string imageUrl   = GetStr(node, "ImageURL");

            // ── Sensor node ──────────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(sensorId))
            {
                currentHardware?.Sensors.Add(new Sensor { Name = text, Type = type, Id = sensorId });
                return; // sensors never have sensor children
            }

            // ── Determine what to do with this container node ────────────────────────
            Hardware nextHardware = currentHardware;

            if (!string.IsNullOrEmpty(hardwareId))
            {
                // Hardware node: only hardware nodes carry a HardwareId field.
                var hw = new Hardware
                {
                    Name = text,
                    Type = HardwareTypeFromImageUrl(imageUrl) // normalized to WMI-style names
                };

                allHardware.Add(hw); // flat list used by FindSensorId

                if (currentHardware != null)
                    currentHardware.SubHardware.Add(hw); // maintain parent→child relationship

                nextHardware = hw;
            }
            // else: root / computer container, sensor-type group → pass through

            // ── Recurse into children ─────────────────────────────────────────────────
            if (node.ContainsKey("Children") && node["Children"] is ArrayList children)
                foreach (var child in children)
                    if (child is Dictionary<string, object> childDict)
                        WalkNode(childDict, nextHardware, allHardware);
        }

        private static string GetStr(Dictionary<string, object> node, string key) =>
            node.ContainsKey(key) ? (node[key]?.ToString() ?? "") : "";

        private static bool TryParseValue(string raw, out double result)
        {
            if (!string.IsNullOrEmpty(raw))
            {
                int space = raw.IndexOf(' ');
                string num = space >= 0 ? raw.Substring(0, space) : raw;
                if (double.TryParse(num, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out result))
                    return true;
            }
            result = 0;
            return false;
        }

        // Scans the raw data.json string for sensor nodes and extracts Value/Min/Max into
        // a cache. Works on the raw string to avoid allocating the large intermediate
        // Dictionary<string,object>/ArrayList object graph that JavaScriptSerializer produces.
        // Sensor nodes in LHM are flat JSON objects (no nested '{...}'), so finding the
        // enclosing braces via a simple linear scan is reliable.
        private static Dictionary<string, Dictionary<string, double>> ParseSensorValuesFromJson(string json)
        {
            var cache = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
            int pos = 0;

            while (pos < json.Length)
            {
                int sidPos = json.IndexOf("\"SensorId\"", pos, StringComparison.Ordinal);
                if (sidPos < 0) break;

                // Scan backwards for the opening '{' of the enclosing sensor object.
                int objStart = sidPos - 1;
                while (objStart >= 0 && json[objStart] != '{' && json[objStart] != '}') objStart--;
                if (objStart < 0 || json[objStart] != '{') { pos = sidPos + 10; continue; }

                // Scan forward for the closing '}' (sensor nodes are flat, no nested braces).
                int objEnd = json.IndexOf('}', sidPos + 10);
                if (objEnd < 0) break;

                string sensorId = ReadJsonString(json, sidPos + 10, objEnd);
                if (string.IsNullOrEmpty(sensorId)) { pos = objEnd + 1; continue; }

                var vals = new Dictionary<string, double>(3, StringComparer.OrdinalIgnoreCase);
                ExtractNumericField(json, objStart, objEnd, "\"Value\"", "value", vals);
                ExtractNumericField(json, objStart, objEnd, "\"Min\"",   "min",   vals);
                ExtractNumericField(json, objStart, objEnd, "\"Max\"",   "max",   vals);

                cache[sensorId] = vals;
                pos = objEnd + 1;
            }
            return cache;
        }

        // Starting at 'from', skips ':' and surrounding whitespace, then reads the
        // quoted JSON string value up to (but not past) 'limit'.
        private static string ReadJsonString(string json, int from, int limit)
        {
            int i = from;
            while (i < limit && json[i] != ':') i++;
            if (i >= limit) return null;
            i++; // skip ':'
            while (i < limit && json[i] != '"') i++;
            if (i >= limit) return null;
            i++; // skip opening '"'
            int start = i;
            while (i < limit && json[i] != '"') i++;
            if (i >= limit) return null;
            return json.Substring(start, i - start);
        }

        private static void ExtractNumericField(
            string json, int from, int to,
            string fieldKey, string cacheKey,
            Dictionary<string, double> vals)
        {
            int count = to - from;
            if (count <= 0) return;
            int pos = json.IndexOf(fieldKey, from, count, StringComparison.Ordinal);
            if (pos < 0) return;
            string raw = ReadJsonString(json, pos + fieldKey.Length, to);
            if (raw != null && TryParseValue(raw, out double d)) vals[cacheKey] = d;
        }

        private void UpdateSensorValues()
        {
            if ((DateTime.UtcNow - _lastSensorRefresh).TotalMilliseconds < 800) return;
            _lastSensorRefresh = DateTime.UtcNow;
            try
            {
                string dataJson = Http.GetStringAsync(Url + "/data.json").GetAwaiter().GetResult();
                _sensorCache = ParseSensorValuesFromJson(dataJson);
            }
            catch (Exception ex)
            {
                Api.Log(API.LogType.Error,
                    "LibreHardwareMonitor: failed to refresh sensor values: " + ex.Message);
            }
        }

        // Maps an LHM ImageURL (e.g. "images_icon/gpu-amd.png") to the equivalent
        // WMI HardwareType name (e.g. "GpuAmd") so that skins ported from the WMI
        // plugin work without changes.
        private static string HardwareTypeFromImageUrl(string imageUrl)
        {
            string file = Path.GetFileNameWithoutExtension(imageUrl).ToLowerInvariant();
            switch (file)
            {
                case "cpu":           return "Cpu";
                case "gpu-amd":       return "GpuAmd";
                case "gpu-nvidia":    return "GpuNvidia";
                case "gpu-intel":     return "GpuIntel";
                case "hdd":
                case "nvme":
                case "ssd":
                case "storage":       return "Storage";
                case "ram":
                case "memory":        return "Memory";
                case "mainboard":
                case "motherboard":   return "Motherboard";
                case "nic":
                case "network":       return "Network";
                case "battery":       return "Battery";
                case "psu":           return "Psu";
                case "chip":
                case "superio":
                case "lpc":           return "SuperIO";
                case "ec":            return "EmbeddedController";
                default:              return file; // pass through unknown types as-is
            }
        }

        // Finds the sensor ID that matches the given hardware/sensor filters and indexes.
        // The flat _hardware list is in DFS order, mirroring the order hardware appears
        // in the LHM tree — the same ordering the WMI version produced.
        internal string FindSensorId(string hwType, string hwName, int hwIndex,
                                     string sType,  string sName,  int sIndex)
        {
            if (_hardware.Count == 0) RefreshHardwareTree();

            // Step 1 — collect hardware nodes matching the hardware filter, in order.
            var matchedHw = new List<Hardware>();
            foreach (var hw in _hardware)
            {
                bool match =
                    (string.IsNullOrEmpty(hwType) || hw.Type.Equals(hwType, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(hwName) || hw.Name.Equals(hwName, StringComparison.OrdinalIgnoreCase));
                if (match) matchedHw.Add(hw);
            }

            if (hwIndex >= matchedHw.Count)
            {
                Api.Log(API.LogType.Warning,
                    $"LibreHardwareMonitor: no hardware at index {hwIndex} " +
                    $"(HardwareType='{hwType}', HardwareName='{hwName}')");
                return null;
            }

            Hardware target = matchedHw[hwIndex];

            // Step 2 — within that hardware, find the sIndex-th matching sensor.
            int found = 0;
            foreach (var s in target.Sensors)
            {
                bool match =
                    (string.IsNullOrEmpty(sType) || s.Type.Equals(sType, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(sName) || s.Name.Equals(sName, StringComparison.OrdinalIgnoreCase));

                if (match)
                {
                    if (found == sIndex) return s.Id;
                    found++;
                }
            }

            Api.Log(API.LogType.Warning,
                $"LibreHardwareMonitor: sensor not found on '{target.Name}' " +
                $"(SensorType='{sType}', SensorName='{sName}', SensorIndex={sIndex})");
            return null;
        }

        internal double GetSensorValue(string sensorId, string valueKey)
        {
            UpdateSensorValues();

            if (_sensorCache.TryGetValue(sensorId, out var values) &&
                values.TryGetValue(valueKey, out double cached))
                return cached;

            Api.Log(API.LogType.Warning,
                $"LibreHardwareMonitor: no cached value for sensor {sensorId} (key='{valueKey}')");
            return -1;
        }
    }

    class ChildMeasure
    {
        private ParentMeasure _parent;
        private string _sensorId;
        private string _valueKey;
        private API    _api;

        private string _hwType, _hwName, _sType, _sName;
        private int    _hwIndex, _sIndex;

        internal void Reload(API rm)
        {
            _api      = rm;
            _valueKey = rm.ReadString("SensorValueName", "Value").ToLowerInvariant();
            _hwType   = rm.ReadString("HardwareType", "");
            _hwName   = rm.ReadString("HardwareName", "");
            _hwIndex  = rm.ReadInt("HardwareIndex", 0);
            _sType    = rm.ReadString("SensorType", "");
            _sName    = rm.ReadString("SensorName", "");
            _sIndex   = rm.ReadInt("SensorIndex", 0);

            IntPtr skin       = rm.GetSkin();
            string parentName = rm.ReadString("Parent", "");

            if (!string.IsNullOrEmpty(parentName))
            {
                _parent = ParentMeasure.FindByName(skin, parentName);
                if (_parent == null)
                    _api.Log(API.LogType.Error,
                        $"LibreHardwareMonitor: no URL= measure named '{parentName}' found.");
            }
            else
            {
                _parent = ParentMeasure.FindInSkin(skin)
                       ?? ParentMeasure.GetOrCreateImplicit(skin, _api);
            }

            if (_parent == null) return;

            _sensorId = _parent.FindSensorId(_hwType, _hwName, _hwIndex, _sType, _sName, _sIndex);
            if (_sensorId == null)
                _api.Log(API.LogType.Warning,
                    $"LibreHardwareMonitor: could not resolve sensor " +
                    $"(HardwareName='{_hwName}', SensorName='{_sName}', SensorType='{_sType}')");
            else
                _api.Log(API.LogType.Debug,
                    $"LibreHardwareMonitor: '{_sName}' resolved to {_sensorId}");
        }

        internal double Update()
        {
            if (_parent == null) return -1;

            if (_sensorId == null)
            {
                _sensorId = _parent.FindSensorId(_hwType, _hwName, _hwIndex, _sType, _sName, _sIndex);
                if (_sensorId == null) return -1;
            }

            return _parent.GetSensorValue(_sensorId, _valueKey);
        }
    }

    public class Measure
    {
        private bool          _isParent;
        private ParentMeasure _parentMeasure;
        private ChildMeasure  _childMeasure;

        internal void Reload(API rm)
        {
            string url = rm.ReadString("URL", "");
            _isParent  = !string.IsNullOrEmpty(url);

            if (_isParent)
            {
                if (_parentMeasure == null) _parentMeasure = new ParentMeasure();
                _parentMeasure.Reload(rm, url);
            }
            else
            {
                if (_childMeasure == null) _childMeasure = new ChildMeasure();
                _childMeasure.Reload(rm);
            }
        }

        internal double Update()
        {
            if (_isParent) return 0.0;
            return _childMeasure?.Update() ?? -1;
        }

        internal void Dispose()
        {
            if (_isParent) _parentMeasure?.Dispose();
        }
    }


    public static class Plugin
    {
        [DllExport]
        public static void Initialize(ref IntPtr data, IntPtr rm)
        {
            data = GCHandle.ToIntPtr(GCHandle.Alloc(new Measure()));
        }

        [DllExport]
        public static void Finalize(IntPtr data)
        {
            GCHandle handle = GCHandle.FromIntPtr(data);
            ((Measure)handle.Target).Dispose();
            handle.Free();
        }

        [DllExport]
        public static void Reload(IntPtr data, IntPtr rm, ref double maxValue)
        {
            Measure measure = (Measure)GCHandle.FromIntPtr(data).Target;
            measure.Reload(new API(rm));
        }

        [DllExport]
        public static double Update(IntPtr data)
        {
            Measure measure = (Measure)GCHandle.FromIntPtr(data).Target;
            return measure.Update();
        }

#if DLLEXPORT_GETSTRING
        [DllExport]
        public static IntPtr GetString(IntPtr data)
        {
            Measure measure = (Measure)GCHandle.FromIntPtr(data).Target;
            return Rainmeter.StringBuffer.Update(measure.GetString());
        }
#endif

#if DLLEXPORT_EXECUTEBANG
        [DllExport]
        public static void ExecuteBang(IntPtr data, IntPtr args)
        {
            Measure measure = (Measure)GCHandle.FromIntPtr(data).Target;
            measure.ExecuteBang(Marshal.PtrToStringUni(args));
        }
#endif
    }
}