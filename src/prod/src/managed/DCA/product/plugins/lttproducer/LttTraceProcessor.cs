// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricDCA
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics;
    using System.Fabric.Common.Tracing;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Fabric.Dca;
    using LttngReader;
    using Tools.EtlReader;

    internal class LttTraceProcessor
    {
        private const string TableEventsConfigFileName = "TableEvents.config";
        private const string TableFileExtensionToProcess = "table";

        // Repository of Windows Fabric ETW manifests
        private static readonly WFManifestRepository WinfabManifestRepository = new WFManifestRepository();

        // Service Fabric manifests that we know about
        private readonly WinFabricManifestManager winFabManifestMgr;

        // ETL file sinks
        private readonly ReadOnlyCollection<IEtlFileSink> sinksIEtlFile;
        private readonly ReadOnlyCollection<ICsvFileSink> sinksICsvFile;
        private readonly ManifestCache etwManifestCache = new ManifestCache(Utility.DcaWorkFolder);
        private readonly string dtrTracesDirectory;

        // Tag used to represent the source of the log message
        private readonly string logSourceId;

        // Object used for tracing
        private readonly FabricEvents.ExtensionsEvents traceSource;

        // whether table files should be generated
        private readonly bool createTableFiles;

        // Whitelist of table events
        private readonly HashSet<string> tableEventsWhiteList;

        internal LttTraceProcessor(
            FabricEvents.ExtensionsEvents traceSource,
            string logSourceId,
            string dtrTracesDir,
            ReadOnlyCollection<IEtlFileSink> sinksIEtlFile,
            ReadOnlyCollection<ICsvFileSink> sinksICsvFile)
        {
            this.traceSource = traceSource;
            this.logSourceId = logSourceId;
            this.dtrTracesDirectory = dtrTracesDir;

            this.sinksIEtlFile = sinksIEtlFile;
            this.sinksICsvFile = sinksICsvFile;

            this.createTableFiles = sinksICsvFile.Any();

            this.winFabManifestMgr = this.InitializeWinFabManifestManager();
            
            // initialize white list of table events from file
            if (this.createTableFiles)
            {
                this.tableEventsWhiteList = this.CreateTableEventsFromConfigFile(Path.Combine(Directory.GetCurrentDirectory(), TableEventsConfigFileName));
            }
            else
            {
                this.tableEventsWhiteList = new HashSet<string>();
            }

            this.traceSource.WriteInfo(
                this.logSourceId,
                "Creating table events file set to {0}. The number of whitelist events parsed is {1}",
                this.createTableFiles,
                this.tableEventsWhiteList.Count);

            // Provide the sinks a reference to the manifest cache.
            this.OnEtwManifestCacheAvailable();
        }

        private WinFabricManifestManager InitializeWinFabManifestManager()
        {
            // Figure out which Service Fabric manifests we have.
            // Try the repository first ...
            string manifestFileDirectory;
            try
            {
                manifestFileDirectory = LttTraceProcessor.WinfabManifestRepository.RepositoryPath;
            }
            catch (Exception)
            {
                // If we couldn't access the repository for any reason, fall back to
                // the manifests that we shipped with.
                manifestFileDirectory = Path.GetDirectoryName(Utility.DcaProgramDirectory);
            }

            string[] manifestFiles = Directory.GetFiles(manifestFileDirectory, "*.man", SearchOption.TopDirectoryOnly);

            WinFabricManifestManager manifestManager = null;
            try
            {
                manifestManager = new WinFabricManifestManager(
                                               manifestFiles,
                                               this.LoadManifest,
                                               this.etwManifestCache.UnloadManifest);
            }
            catch (Exception e)
            {
                this.traceSource.WriteError(
                    this.logSourceId,
                    "Exception encountered while identifying the Windows Fabric manifests available. Exception information: {0}",
                    e);
                return manifestManager;
            }

            if (!manifestManager.FabricManifests.Any())
            {
                this.traceSource.WriteError(
                    this.logSourceId,
                    "No Fabric manifests were found.");
            }

            if (!manifestManager.LeaseLayerManifests.Any())
            {
                this.traceSource.WriteError(
                    this.logSourceId,
                    "No lease layer manifests were found.");
            }

            if (!manifestManager.KtlManifests.Any())
            {
                this.traceSource.WriteError(
                    this.logSourceId,
                    "No KTL manifests were found.");
            }

            return manifestManager;
        }

        private HashSet<string> CreateTableEventsFromConfigFile(string configFilePath)
        {
            HashSet<string> whiteList = new HashSet<string>();
            int line = 1;

            try
            {
                using (StreamReader configFile = File.OpenText(configFilePath))
                {
                    while (configFile.Peek() > 0)
                    {
                        string configLine = configFile.ReadLine();

                        // splitting line on :
                        string [] tokens = configLine.Split(':');

                        if (tokens.Length > 0)
                        {
                            string taskEventName = tokens[0];

                            // adding TaskName.EventName to whitelist
                            whiteList.Add(taskEventName);

                            this.traceSource.WriteInfo(
                                this.logSourceId,
                                "Whitelisted event for .table file TaskName.EventName = {0}",
                                taskEventName);
                        }
                        else
                        {
                            this.traceSource.WriteWarning(
                                this.logSourceId,
                                "Error parsing Table events config file {0} at line {1} with the following content {2}. Rule will be skipped for whitelisting",
                                configFilePath,
                                line,
                                configLine);
                        }

                        line++;
                    }
                }
            }
            catch (Exception e)
            {
                this.traceSource.WriteError(
                    this.logSourceId,
                    "Exception encountered while initializing Table events whitelist from file at {0}. {1}",
                    configFilePath,
                    e);
            }

            return whiteList;
        }

        // TODO This eventually needs to be moved to its own Consumer so event is not decoded more than once.
        private void WriteEventToCsvAndTableFiles(EventRecord eventRecord, string lttUnstructuredTextEventString, string taskNameEventNameFromUnstructured, bool isUnstructuredLtt, StreamWriter fileStreamDtr, StreamWriter fileStreamTable)
        {
            DecodedEtwEvent decodedEvent;
            string taskNameEventName = taskNameEventNameFromUnstructured;

            decodedEvent.StringRepresentation = null;
            bool decodingFailed = false;

            // Test whether event needs to be decoded.
            if (!isUnstructuredLtt)
            {
                try
                {
                    string originalEventName;
                    if (false == this.DecodeEvent(ref eventRecord, out decodedEvent, out originalEventName))
                    {
                        // Our attempt to decode the event failed
                        decodingFailed = true;
                    }

                    // TODO - to be removed after transitioning for structured traces in Linux
                    taskNameEventName = $"{decodedEvent.TaskName}.{originalEventName}";
                }
                catch (Exception e)
                {
                        this.traceSource.WriteError(
                            this.logSourceId,
                            "Exception encountered while decoding ETW event. The event will be skipped. Event ID: {0}, Task: {1}, Level: {2}. Exception information: {3}",
                            eventRecord.EventHeader.EventDescriptor.Id,
                            eventRecord.EventHeader.EventDescriptor.Task,
                            eventRecord.EventHeader.EventDescriptor.Level,
                            e);
                    decodingFailed = true;
                }
            }
            else
            {
                // legacy Service Fabric traces which consisted only of Unstructured traces.
                decodedEvent = new DecodedEtwEvent() { StringRepresentation = lttUnstructuredTextEventString};
            }

            if (false == decodingFailed)
            {
                // Our attempt to decode the event succeeded. Put it
                // writing to dtr file
                string replacedLFStringRepresentation = decodedEvent.StringRepresentation.Replace("\r\n", "\r\t").Replace("\n", "\t");

                fileStreamDtr.Write(replacedLFStringRepresentation);
                fileStreamDtr.Write("\r\n");

                if (fileStreamTable != null && this.tableEventsWhiteList.Contains(taskNameEventName))
                {
                    fileStreamTable.Write(replacedLFStringRepresentation);
                    fileStreamTable.Write("\r\n");
                }
            }
        }

        internal void EnsureCorrectWinFabManifestVersionLoaded(string lttngFolderFullPath)
        {
            try
            {
                bool exactMatch;
                if (this.winFabManifestMgr != null)
                {
                    this.winFabManifestMgr.EnsureCorrectWinFabManifestVersionLoaded(
                        LttTraceProcessor.GetLttngTracesSFVersionFromFolder(lttngFolderFullPath),
                        out exactMatch);

                    if (false == exactMatch)
                    {
                        this.traceSource.WriteWarning(
                            this.logSourceId,
                            "Exact manifest match not found for ETL file {0}. We will make a best-effort attempt to decode it with the manifests available.",
                            lttngFolderFullPath);
                    }
                }
                else
                {
                    this.traceSource.WriteError(
                    this.logSourceId,
                    "Manifest manager has not been initialized");
                }
            }
            catch (Exception e)
            {
                this.traceSource.WriteError(
                    this.logSourceId,
                    "Exception encountered while identifying the Service Fabric manifests available. Exception information: {0}",
                    e);
            }
        }

        internal ulong ProcessTraces(string lttTracesDirectory, bool seekTimestamp, ulong timestampUnixEpochNanoSec)
        {
            IntPtr ctx;
            IntPtr ctf_iter;

            this.traceSource.WriteInfo(
                this.logSourceId,
                "Starting to process Ltt traces at: {0} - starting at timestamp: {1}",
                lttTracesDirectory,
                (new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds((double)(timestampUnixEpochNanoSec/1000000)).ToString("o")));
            StringBuilder lttTracesDir = new StringBuilder();
            lttTracesDir.Append(lttTracesDirectory);

            // initializing for reading traces
            int t_handle_id = LttngReaderBindings.initialize_trace_processing(lttTracesDir, out ctx, out ctf_iter, seekTimestamp, timestampUnixEpochNanoSec);

            if (t_handle_id < 0)
            {
                this.traceSource.WriteError(
                    this.logSourceId,
                    "Error ({0}) occurred while trying to process Ltt trace folder file {1}",
                    t_handle_id,
                    lttTracesDir.ToString());
                return timestampUnixEpochNanoSec;
            }

            // reading traces
            int err;
            EventRecord eventRecord = new EventRecord();
            StringBuilder unstructuredTextString = new StringBuilder(64000);
            ulong lastReadTimestamp = 0;
            bool isUnstructured = false;
            ulong numEventsRead = 0;

            // needed for table event filtering for unstructured events in linux
            StringBuilder taskNameEventName = new StringBuilder(200);

            string baseFileName = DateTime.UtcNow.ToString("yyyy-MM-dd_HH:mm:ss");
            string csvOutputFilePath = Path.Combine(this.dtrTracesDirectory, baseFileName + ".trace");
            string tableOutputFilePath = Path.Combine(this.dtrTracesDirectory, baseFileName + ".table1");
            string csvOutputFileFinishedPath = Path.Combine(this.dtrTracesDirectory, baseFileName + ".dtr");
            string tableOutputFileFinishedPath = Path.Combine(this.dtrTracesDirectory, baseFileName + ".table");

            csvOutputFilePath = Path.Combine(this.dtrTracesDirectory, csvOutputFilePath);

            // reading event by event using libbabeltrace API and writing it to CSV file
            using (StreamWriter fileStreamDtr = new StreamWriter(csvOutputFilePath))
            using (StreamWriter fileStreamTable = (this.createTableFiles ? new StreamWriter(tableOutputFilePath) : null))
            {
                while ((err = LttngReaderBindings.read_next_event(ref eventRecord, unstructuredTextString, taskNameEventName, ctf_iter, ref lastReadTimestamp, ref isUnstructured)) == 0)
                {
                    numEventsRead++;
                    timestampUnixEpochNanoSec = lastReadTimestamp;

                    this.DeliverEventToSinks(ref eventRecord, unstructuredTextString, isUnstructured);
                    this.WriteEventToCsvAndTableFiles(eventRecord, unstructuredTextString.ToString(), taskNameEventName.ToString(), isUnstructured, fileStreamDtr, fileStreamTable);
                }
            }

            // cleaning up
            LttngReaderBindings.finalize_trace_processing(ctx, ctf_iter, t_handle_id);

            // check for failure in iterator
            if (err != -2)
            {
                this.traceSource.WriteError(
                    this.logSourceId,
                    "Error ({0}) when trying to get next ctf_iterator", err);
                return timestampUnixEpochNanoSec;
            }

            // renaming files to their final names after finishing processing.
            var filePairs = new [] { new { Temp = csvOutputFilePath, Finished = csvOutputFileFinishedPath }, new { Temp = tableOutputFilePath, Finished = tableOutputFileFinishedPath }};
            foreach (var fp in filePairs)
            {
                try
                {
                    if (File.Exists(fp.Temp))
                    {
                        File.Move(fp.Temp, fp.Finished);
                    }
                }
                catch (Exception e)
                {
                    this.traceSource.WriteError(
                        this.logSourceId,
                        "Exception encountered while moving trace and table files ({0} and {1}). {2}",
                        fp.Temp,
                        fp.Finished,
                        e);
                }
            }

            this.traceSource.WriteInfo(
                this.logSourceId,
                "Finished to process Ltt traces at: {0} - finished at timestamp: {1} - {2} events read",
                lttTracesDirectory,
                (new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds((double)(timestampUnixEpochNanoSec/1000000)).ToString("o")),
                numEventsRead);


            return timestampUnixEpochNanoSec;
        }

        // TODO - Uncomment code below when totally transitioned to structured traces
        private void DeliverEventToSinks(ref EventRecord eventRecord, StringBuilder unstructuredTextString, bool isUnstructured)
        {
            var decodedEvent = new DecodedEtwEvent();
            var decodingAttempted = false;
            var decodingFailed = false;

            List<EventRecord> rawEventList = null;
            List<DecodedEtwEvent> decodedEventList = null;
            foreach (var sink in this.sinksIEtlFile)
            {
                if (sink.NeedsDecodedEvent)
                {
                    // This consumer needs the decoded event
                    if (false == decodingAttempted)
                    {
                        // We haven't yet attempted to decode the event. Attempt
                        // it now.
                        // For handling Linux legacy unstructured traces only
                        if (isUnstructured == true)
                        {
                            decodedEvent.StringRepresentation = unstructuredTextString.ToString();
                            decodingFailed = false;
                            decodingAttempted = true;
                        }
                        else
                        {
                            try
                            {
                                string unusedOriginalEventName;

                                if (false == this.DecodeEvent(ref eventRecord, out decodedEvent, out unusedOriginalEventName))
                                {
                                    // Our attempt to decode the event failed
                                    decodingFailed = true;
                                }
                            }
                            catch (Exception e)
                            {
                                this.traceSource.WriteError(
                                    this.logSourceId,
                                    "Exception encountered while decoding Ltt event. The event will be skipped. Event ID: {0}, Task: {1}, Level: {2}. Exception information: {3}",
                                    eventRecord.EventHeader.EventDescriptor.Id,
                                    eventRecord.EventHeader.EventDescriptor.Task,
                                    eventRecord.EventHeader.EventDescriptor.Level,
                                    e);
                                decodingFailed = true;
                            }
                        }

                        if (false == decodingFailed)
                        {
                            // Our attempt to decode the event succeeded. Put it
                            // in the event list.
                            Debug.Assert(null == decodedEventList, "Decoded event list should not be initialized previously.");
                            decodedEventList = new List<DecodedEtwEvent> { decodedEvent };
                        }

                        decodingAttempted = true;
                        
                    }

                    if (false == decodingFailed)
                    {
                        sink.OnEtwEventsAvailable(decodedEventList);
                    }
                }
                else
                {
                    if (isUnstructured == false)
                    {
                        // This consumer needs the raw event
                        if (null == rawEventList)
                        {
                            // Put the event in the event list
                            rawEventList = new List<EventRecord> { eventRecord };
                        }
                        if (sink is IEtlFileSink)
                        {
                            IEtlFileSink sinkEtl = sink as IEtlFileSink;
                            sinkEtl.OnEtwEventsAvailable(rawEventList);
                        }
                    }
                }
            }
        }

        private void OnEtwManifestCacheAvailable()
        {
            foreach (var sink in this.sinksIEtlFile)
            {
                sink.SetEtwManifestCache(this.etwManifestCache);
            }
        }

        private bool DecodeEvent(ref EventRecord eventRecord, out DecodedEtwEvent decodedEvent, out string originalEventName)
        {
            decodedEvent = new DecodedEtwEvent { EventRecord = eventRecord };
            originalEventName = string.Empty;

            if (false == ManifestCache.IsStringEvent(eventRecord))
            {
                var eventDefinition = this.etwManifestCache.GetEventDefinition(
                    eventRecord);
                if (null == eventDefinition)
                {
                    if (eventRecord.EventHeader.EventDescriptor.Task != (ushort)FabricEvents.EventSourceTaskId)
                    {
                        // We couldn't decode this event. Skip it.
                        this.traceSource.WriteError(
                            this.logSourceId,
                            "Unable to decode ETW event. The event will be skipped. Event ID: {0}, Task: {1}, Level: {2}.",
                            eventRecord.EventHeader.EventDescriptor.Id,
                            eventRecord.EventHeader.EventDescriptor.Task,
                            eventRecord.EventHeader.EventDescriptor.Level);
                    }

                    return false;
                }

                if (eventDefinition.IsChildEvent)
                {
                    // Try to format the event. This causes the ETL reader to save information
                    // about this event. That information is later retrieved when the parent
                    // event is processed.
                    string childEventType;
                    string childEventText;
                    var childStringRepresentation = this.etwManifestCache.FormatEvent(
                        eventRecord,
                        out childEventType,
                        out childEventText);

                    // Only parent events can be decoded. Child events supply additional
                    // information about the parent event and cannot be decoded on their
                    // own.
                    return false;
                }

                decodedEvent.TaskName = eventDefinition.TaskName;
                originalEventName = eventDefinition.OriginalEventName;
            }
            else
            {
                decodedEvent.TaskName = EventFormatter.StringEventTaskName;
            }

            decodedEvent.Timestamp = DateTime.FromFileTimeUtc(eventRecord.EventHeader.TimeStamp);
            decodedEvent.Level = eventRecord.EventHeader.EventDescriptor.Level;
            decodedEvent.ThreadId = eventRecord.EventHeader.ThreadId;
            decodedEvent.ProcessId = eventRecord.EventHeader.ProcessId;
            decodedEvent.StringRepresentation = this.etwManifestCache.FormatEvent(
                eventRecord,
                out decodedEvent.EventType,
                out decodedEvent.EventText,
                0);
            if (null == decodedEvent.StringRepresentation)
            {
                if (eventRecord.EventHeader.EventDescriptor.Task !=  (ushort)FabricEvents.EventSourceTaskId)
                {
                    // We couldn't decode this event. Skip it.
                    this.traceSource.WriteError(
                        this.logSourceId,
                        "Unable to decode ETW event. The event will be skipped. Event ID: {0}, Task: {1}, Level: {2}.",
                        eventRecord.EventHeader.EventDescriptor.Id,
                        eventRecord.EventHeader.EventDescriptor.Task,
                        eventRecord.EventHeader.EventDescriptor.Level);
                    return false;
                }
            }

            // in case it is a string event and the eventName is not give by the eventDefinition
            if (string.IsNullOrEmpty(originalEventName))
            {
                originalEventName = decodedEvent.EventType;
            }

            // If this is an FMM event, update the last timestamp of FMM events
           if (decodedEvent.TaskName.Equals(Utility.FmmTaskName))
           {
               Utility.LastFmmEventTimestamp = decodedEvent.Timestamp;
           }

            return true;
        }

        private void LoadManifestWorker(string manifestFileName)
        {
            this.etwManifestCache.LoadManifest(manifestFileName);
        }

        private void LoadManifest(string manifestFileName)
        {
            try
            {
                Utility.PerformIOWithRetries(
                    this.LoadManifestWorker,
                    manifestFileName);
                
                this.traceSource.WriteInfo(
                    this.logSourceId,
                    "Loaded manifest {0}",
                    manifestFileName);
            }
            catch (Exception e)
            {
                this.traceSource.WriteExceptionAsError(
                    this.logSourceId,
                    e,
                    "Failed to load manifest {0}.",
                    manifestFileName);
            }
        }

        internal void LoadDefaultManifests()
        {
            // Get the directory where the default manifests are located
            var manifestFileDirectory = Utility.DcaProgramDirectory;

            // Load the manifests
            foreach (string manifestFileName in WinFabricManifestManager.DefaultManifests)
            {
                string manifestFile = Path.Combine(
                                          manifestFileDirectory,
                                          manifestFileName);
                this.LoadManifest(manifestFile);
            }
        }

        internal void UnloadDefaultManifests()
        {
            // Get the directory where the default manifests are located
            var manifestFileDirectory = Utility.DcaProgramDirectory;

            // Load the manifests
            foreach (string manifestFileName in WinFabricManifestManager.DefaultManifests)
            {
                string manifestFile = Path.Combine(
                                          manifestFileDirectory,
                                          manifestFileName);
                this.etwManifestCache.UnloadManifest(manifestFile);
            }
        }

        private static string GetLttngTracesSFVersionFromFolder(string lttngSessionFolder)
        {
            // extract folder name from full path
            string folderName = (new DirectoryInfo(lttngSessionFolder)).Name;

            Group version = Regex.Match(folderName, @"(fabric_traces_\d+\.\d+\.\d+\.\d+)_").Groups[1];

            // extracting version 
            //( if no match, e.g. traces coming from 6.2 the return value is string.Empty)
            return version.ToString();
        }
    }
} 
