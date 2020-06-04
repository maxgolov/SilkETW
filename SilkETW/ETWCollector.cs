﻿using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Parsers;
using System.Xml;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Threading;

namespace SilkETW
{
    class ETWCollector
    {
        
        public static void StartTrace(CollectorType CollectorType, ulong TraceKeywords, OutputType OutputType, String Path, FilterOption FilterOption, Object FilterValue, String YaraScan, YaraOptions YaraOptions, String ProviderName = "", UserTraceEventLevel UserTraceEventLevel = UserTraceEventLevel.Informational)
        {
            // Is elevated?
            if (TraceEventSession.IsElevated() != true)
            {
                SilkUtility.ReturnStatusMessage("[!] The collector must be run as Administrator..", ConsoleColor.Red);
                return;
            }

            // Print status
            SilkUtility.ReturnStatusMessage("[>] Starting trace collector (Ctrl-c to stop)..", ConsoleColor.Yellow);
            SilkUtility.ReturnStatusMessage("[?] Events captured: 0", ConsoleColor.Green); // We will update this dynamically

            // The kernel collector has naming requirements
            if (CollectorType == CollectorType.Kernel)
            {
                SilkUtility.EventParseSessionName = KernelTraceEventParser.KernelSessionName;
            } else
            {
                // We add a GUID in case of concurrent SilkETW execution
                String RandId = Guid.NewGuid().ToString();
                // SilkUtility.EventParseSessionName = ("SilkETWUserCollector_" + RandId);

                // TODO: add command-line parameter to allow for only one tracer at a time
                SilkUtility.EventParseSessionName = ("SilkETWUserCollector");
            }

            // Process 12,345 as 12345
            CultureInfo ci = new CultureInfo("");
            Thread.CurrentThread.CurrentCulture = ci;

            // Create trace session
            using (var TraceSession = new TraceEventSession(SilkUtility.EventParseSessionName))
            {
                // The collector cannot survive process termination (safeguard)
                TraceSession.StopOnDispose = true;

                // Create event source
                using (var EventSource = new ETWTraceEventSource(SilkUtility.EventParseSessionName, TraceEventSourceType.Session))
                {
                    // Ctrl-c callback handler
                    SilkUtility.SetupCtrlCHandler(() =>
                    {
                        if (OutputType == OutputType.eventlog)
                        {
                            SilkUtility.WriteEventLogEntry("{\"Collector\":\"Stop\",\"Error\":false}", EventLogEntryType.SuccessAudit, EventIds.StopOk, Path);
                        }
                        TerminateCollector();
                    });

                    // A DynamicTraceEventParser can understand how to read the embedded manifests that occur in the dataStream
                    var EventParser = new DynamicTraceEventParser(EventSource);

                    Action<TraceEvent> action = delegate (TraceEvent data)
                    {
                        bool isEventWriteString = false;
                        bool isEventWriteStringXML = false;
                        string payload = "";

                        //
                        // Handler for unmanifested events generated with EventWriteString
                        //
                        if (data.EventName == "EventWriteString")
                        {
                            isEventWriteString = true;
                            try
                            {
                                UTF8Encoding utf8 = new UTF8Encoding();
                                byte[] buff = data.EventData();
                                int pos = 0;
                                int count = data.EventDataLength;
                                payload = utf8.GetString(buff, pos, count);
                                isEventWriteStringXML = payload.StartsWith("<Event", StringComparison.CurrentCulture);
                            }
                            catch (Exception ex)
                            {
                                // I am very cross with you... please send me good payloads next time!
                            }
                        }

                        // It's a bit ugly but ... ¯\_(ツ)_/¯
                        if (FilterOption != FilterOption.None)
                        {
                            if (FilterOption == FilterOption.Opcode && (byte)data.Opcode != (byte)FilterValue)
                            {
                                SilkUtility.ProcessEventData = false;
                            } else if (FilterOption == FilterOption.ProcessID && data.ProcessID != (UInt32)FilterValue)
                            {
                                SilkUtility.ProcessEventData = false;
                            } else if (FilterOption == FilterOption.ProcessName && data.ProcessName != (String)FilterValue)
                            {
                                SilkUtility.ProcessEventData = false;
                            } else if (FilterOption == FilterOption.EventName && data.EventName != (String)FilterValue)
                            {
                                SilkUtility.ProcessEventData = false;
                            } else
                            {
                                SilkUtility.ProcessEventData = true;
                            }
                        } else
                        {
                            SilkUtility.ProcessEventData = true;
                        }

                        // Only process/serialize events if they match our filter
                        if (SilkUtility.ProcessEventData)
                        {
                            // Display running event count
                            SilkUtility.RunningEventCount += 1;
                            SilkUtility.UpdateEventCount("[?] Events captured: " + SilkUtility.RunningEventCount);

                            var eRecord = new EventRecordStruct
                            {
                                ProviderGuid = data.ProviderGuid,
                                YaraMatch = new List<String>(),
                                ProviderName = data.ProviderName,
                                EventName = data.EventName,
                                Opcode = data.Opcode,
                                OpcodeName = data.OpcodeName,
                                TimeStamp = data.TimeStamp,
                                ThreadID = data.ThreadID,
                                ProcessID = data.ProcessID,
                                ProcessName = data.ProcessName,
                                PointerSize = data.PointerSize,
                                EventDataLength = data.EventDataLength
                            };

                            // Populate Proc name if undefined
                            if (String.IsNullOrEmpty(eRecord.ProcessName))
                            {
                                try
                                {
                                    eRecord.ProcessName = Process.GetProcessById(eRecord.ProcessID).ProcessName;
                                }
                                catch
                                {
                                    eRecord.ProcessName = "N/A";
                                }
                            }
                            var EventProperties = new Hashtable();

                            // Try to parse event XML
                            Action<string> parseXML = (inString) =>
                            {
                                try
                                {
                                    StringReader XmlStringContent = new StringReader(inString);
                                    XmlTextReader EventElementReader = new XmlTextReader(XmlStringContent);
                                    while (EventElementReader.Read())
                                    {
                                        for (int AttribIndex = 0; AttribIndex < EventElementReader.AttributeCount; AttribIndex++)
                                        {
                                            EventElementReader.MoveToAttribute(AttribIndex);

                                            // Cap maxlen for eventdata elements to 10k
                                            if (EventElementReader.Value.Length > 10000)
                                            {
                                                String DataValue = EventElementReader.Value.Substring(0, Math.Min(EventElementReader.Value.Length, 10000));
                                                EventProperties.Add(EventElementReader.Name, DataValue);
                                            } else
                                            {
                                                EventProperties.Add(EventElementReader.Name, EventElementReader.Value);
                                            }
                                        }
                                    }
                                }
                                catch
                                {
                                    // For debugging (?), never seen this fail
                                    EventProperties.Add("XmlEventParsing", "false");
                                }
                            };

                            // TODO: instead of transforming to XML and parsing, it'd be best to iterate over properties of a dynamic object
                            // JavaScriptSerializer serializer = new JavaScriptSerializer();
                            // dynamic myDynamicObject = serializer.DeserializeObject(json);
                            parseXML(data.ToString());
                            if (isEventWriteStringXML)
                            {
                                parseXML(payload);
                            } else
                            {
                                // TODO: implement support for alternate payload types
                            }
                            // eRecord.XmlEventData = EventProperties;

                            // Serialize to JSON
                            JObject o = (JObject)JToken.FromObject(eRecord);
                            o.Remove("XmlEventData");
                            o.Remove("YaraMatch");
                            foreach (DictionaryEntry s in EventProperties)
                            {
                                var token = o.SelectToken((string)s.Key);
                                var newValue = JToken.FromObject((string)s.Value);
                                if (token != null)
                                {
                                    token.Replace(newValue);
                                }
                                else
                                {
                                    o.Add((string)s.Key, newValue);
                                }
                            }

                            // Rename 'EventName' to 'name'
                            if (o["EventName"] != null)
                            {
                                var name = o["EventName"];
                                o["EventName"].Parent.Remove();
                                o.Add("name", name);
                            }
                            // TODO - remap:
                            // - Opcode
                            // - OpcodeName
                            // - Timestamp

                            String JSONEventData = o.ToString(Newtonsoft.Json.Formatting.None); // Newtonsoft.Json.JsonConvert.SerializeObject(eRecord);
                            int ProcessResult = SilkUtility.ProcessJSONEventData(JSONEventData, OutputType, Path, YaraScan, YaraOptions);

                            // Verify that we processed the result successfully
                            if (ProcessResult != 0)
                            {
                                if (ProcessResult == 1)
                                {
                                    SilkUtility.ReturnStatusMessage("[!] The collector failed to write to file", ConsoleColor.Red);
                                } else if (ProcessResult == 2)
                                {
                                    SilkUtility.ReturnStatusMessage("[!] The collector failed to POST the result", ConsoleColor.Red);
                                } else {
                                    SilkUtility.ReturnStatusMessage("[!] The collector failed write to the eventlog", ConsoleColor.Red);
                                }

                                // Write status to eventlog if dictated by the output type
                                if (OutputType == OutputType.eventlog)
                                {
                                    SilkUtility.WriteEventLogEntry($"{{\"Collector\":\"Stop\",\"Error\":true,\"ErrorCode\":{ProcessResult}}}", EventLogEntryType.Error, EventIds.StopError, Path);
                                }

                                // Shut down the collector
                                TerminateCollector();
                            }
                        }
                    };

                    // Loop events as they arrive without the parser.
                    // TODO: check for duplicates?
                    EventSource.AllEvents += action;

                    // Loop events as they arrive thru the parser
                    EventParser.All += action;

                    // Specify the providers details
                    if (CollectorType == CollectorType.Kernel)
                    {
                        TraceSession.EnableKernelProvider((KernelTraceEventParser.Keywords)TraceKeywords);
                    } else
                    {
                        // Note that the collector doesn't know if you specified a wrong provider name,
                        // the only tell is that you won't get any events ;) 
                        TraceSession.EnableProvider(ProviderName, (TraceEventLevel)UserTraceEventLevel, TraceKeywords);
                    }

                    // Write status to eventlog if dictated by the output type
                    if (OutputType == OutputType.eventlog)
                    {
                        String ConvertKeywords;
                        if (CollectorType == CollectorType.Kernel)
                        {
                            ConvertKeywords = Enum.GetName(typeof(KernelTraceEventParser.Keywords), TraceKeywords);
                        } else
                        {
                            ConvertKeywords = "0x" + String.Format("{0:X}", TraceKeywords);
                        }
                        String Message = $"{{\"Collector\":\"Start\",\"Data\":{{\"Type\":\"{CollectorType}\",\"Provider\":\"{ProviderName}\",\"Keywords\":\"{ConvertKeywords}\",\"FilterOption\":\"{FilterOption}\",\"FilterValue\":\"{FilterValue}\",\"YaraPath\":\"{YaraScan}\",\"YaraOption\":\"{YaraOptions}\"}}}}";
                        SilkUtility.WriteEventLogEntry(Message, EventLogEntryType.SuccessAudit, EventIds.Start, Path);
                    }

                    // Continuously process all new events in the data source
                    EventSource.Process();

                    // Helper to clean up colloector
                    void TerminateCollector()
                    {
                        EventSource.StopProcessing();
                        TraceSession?.Stop();
                        Console.CursorVisible = true;
                        SilkUtility.ReturnStatusMessage("[+] Collector terminated", ConsoleColor.Green);
                    }
                }

            }
        }
    }
}