﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Diagnostics;
using System.Configuration;

namespace Chetch.Utilities.Config
{
    //General config stuff goes here

    //This is specific to the app.config file
    static public class AppConfig
    {
        static bool _readConfig = false;
        static private XDocument _configDoc;
        static private XElement _sysDiagnostics;
        static private XElement _sources;
        static private List<String> _sourceNames;
        static private XElement _sharedListeners;

        static public void ReadConfig(bool forceRead = false)
        {
            if (!_readConfig || forceRead)
            {
                string path = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None).FilePath; //path to app.Config
                _configDoc = XDocument.Load(path);
                _sysDiagnostics = _configDoc.Descendants("system.diagnostics").First<XElement>();
                _sources = _sysDiagnostics.Descendants("sources").FirstOrDefault<XElement>();
                _sharedListeners = _sysDiagnostics.Descendants("sharedListeners").FirstOrDefault<XElement>();

                _sourceNames = new List<String>();
                foreach (var src in _sources.Elements())
                {
                    _sourceNames.Add(src.Attribute("name").Value);
                }
            }
            _readConfig = true;
        }

        static public List<String> SourceNames
        {
            get
            {
                ReadConfig();
                return _sourceNames;
            }

        }

        static public List<String> GetSourceListenerAttributes(String listenerType, String attributeKey)
        {
            ReadConfig();

            var listenerAttributes =
                 from e in _sources.Descendants("add")
                 where e.Parent.Name.LocalName == "listeners" && e.Attribute("type") != null && e.Attribute("type").Value.IndexOf(listenerType) >= 0
                 select e.Attribute(attributeKey).Value;

            return listenerAttributes.ToList<String>();
        }

        static public List<String> GetSourceListenerAttributes(Type t, String attributeKey)
        {
            return GetSourceListenerAttributes(t.Name, attributeKey);
        }

        static public bool VerifyEventLogSources(String logName, bool createIfRequired = true)
        {
            var sourceNames = GetSourceListenerAttributes(typeof(EventLog), "initializeData");
            bool requiresRestart = false;
            foreach (var source in sourceNames)
            {
                if (!EventLog.SourceExists(source))
                {
                    if (!createIfRequired)
                    {
                        throw new Exception("Source " + source + " does not exist.");
                    }

                    EventLog.CreateEventSource(source, logName);
                    //Console.WriteLine("Creating source " + source + " for log " + logName);
                    EventLog.WriteEntry(source, "Created source");
                    requiresRestart = true;
                }
            }

            return !requiresRestart;
        }
    } //end AppConfig class

    static public class TraceSourceManager
    {
        class TraceSourceData
        {
            public String Name;
            public TraceSource TS;
            public Dictionary<String, TraceListener> Listeners = new Dictionary<String, TraceListener>();
            private Dictionary<String, TraceFilter> _listenerOriginalFilters = new Dictionary<String, TraceFilter>();


            public TraceSourceData(String name)
            {
                TS = new TraceSource(name);

                Name = name;
                for (var i = 0; i < TS.Listeners.Count; i++)
                {
                    var l = TS.Listeners[i];
                    Listeners[l.Name] = l;
                    _listenerOriginalFilters[l.Name] = l.Filter;
                }
            }

            public void SetListenerFilter(String listenerName, TraceFilter filter)
            {
                if (Listeners.ContainsKey(listenerName))
                {
                    Listeners[listenerName].Filter = filter;
                }
            }

            public void SetListenerTraceLevel(String listenerName, SourceLevels level)
            {
                SetListenerFilter(listenerName, new EventTypeFilter(level));
            }

            public void TurnOffListener(String listenerName)
            {
                SetListenerTraceLevel(listenerName, SourceLevels.Off);
            }

            public void RestoreListenerFilter(String listenerName)
            {
                if (Listeners.ContainsKey(listenerName))
                {
                    Listeners[listenerName].Filter = _listenerOriginalFilters[listenerName];
                }
            }
        }

        static private List<TraceSourceData> _traceSources = new List<TraceSourceData>();

        /// <summary>
        /// This will instantiate trace sources and then trace this to the specified source.  Pass null if you don't want to trace.
        /// </summary>
        /// <param name="traceTo"></param>
        /// <param name="eventId"></param>
        /*static public void InitFromAppConfig(String traceTo, int eventId = 0)
        {
            AppConfig.ReadConfig();

            var traceOutput = new List<String>();
            if (_traceSources == null)
            {
                _traceSources = new Dictionary<String, TraceSourceData>();
                foreach (var sn in AppConfig.SourceNames)
                {
                    _traceSources[sn] = new TraceSourceData(sn);
                    traceOutput.Add(String.Format("Creating TraceSource instance for source {0}", sn));
                }
            }

            if (traceTo != null && _traceSources.ContainsKey(traceTo))
            {
                var ts = _traceSources[traceTo].TS;
                foreach (var o in traceOutput)
                {
                    ts.TraceEvent(TraceEventType.Information, eventId, o);
                }
            }
        }*/

        static public TraceSource CreateInstance(String name)
        {
            var tsd = new TraceSourceData(name);
            _traceSources.Add(tsd);
            return tsd.TS;
        }


        /// <summary>
        /// Methods for individual trace sources
        /// </summary>
        /// <returns></returns>
        static private TraceSourceData _GetDataFromInstance(TraceSource tsource)
        {
            foreach (var tsd in _traceSources)
            {
                if (tsd.TS == tsource) return tsd;
            }
            return null;
        }

        static public List<String> GetListenerNames(TraceSource tsource)
        {
            var tsd = _GetDataFromInstance(tsource);
            List<String> listenerNames = new List<String>();
            foreach (var ln in tsd.Listeners.Keys)
            {
                listenerNames.Add(ln);
            }
            return listenerNames;
        }

        static public void SetListenersTraceLevel(TraceSource tsource, String listenerName, SourceLevels level)
        {
            var tsd = _GetDataFromInstance(tsource);
            tsd.SetListenerTraceLevel(listenerName, level);
        }

        static public void TurnOffListeners(TraceSource tsource, String listenerName)
        {
            var tsd = _GetDataFromInstance(tsource);
            tsd.TurnOffListener(listenerName);
        }

        static public void RestoreListeners(TraceSource tsource, String listenerName)
        {
            var tsd = _GetDataFromInstance(tsource);
            tsd.RestoreListenerFilter(listenerName);
        }

        /// <summary>
        /// Methods for all trace sources
        /// </summary>
        /// <returns></returns>
        static public List<String> GetListenerNames()
        {
            List<String> listenerNames = new List<String>();
            foreach (var tsd in _traceSources)
            {
                foreach(var ln in tsd.Listeners.Keys)
                {
                    if (!listenerNames.Contains(ln))
                    {
                        listenerNames.Add(ln);
                    }
                }
            }
            return listenerNames;
        }

        static public void SetListenersTraceLevel(String listenerName, SourceLevels level)
        {
            foreach (var tsd in _traceSources)
            {
                tsd.SetListenerTraceLevel(listenerName, level);
            }
        }

        static public void TurnOffListeners(String listenerName)
        {
            foreach (var tsd in _traceSources)
            {
                tsd.TurnOffListener(listenerName);
            }
        }

        static public void RestoreListeners(String listenerName)
        {
            foreach (var tsd in _traceSources)
            {
                tsd.RestoreListenerFilter(listenerName);
            }
        }
    } //end TraceSourceManager class
}
