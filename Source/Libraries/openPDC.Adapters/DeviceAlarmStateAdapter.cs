﻿//******************************************************************************************************
//  DeviceAlarmStateAdapter.cs - Gbtc
//
//  Copyright © 2018, Grid Protection Alliance.  All Rights Reserved.
//
//  Licensed to the Grid Protection Alliance (GPA) under one or more contributor license agreements. See
//  the NOTICE file distributed with this work for additional information regarding copyright ownership.
//  The GPA licenses this file to you under the MIT License (MIT), the "License"; you may not use this
//  file except in compliance with the License. You may obtain a copy of the License at:
//
//      http://opensource.org/licenses/MIT
//
//  Unless agreed to in writing, the subject software distributed under the License is distributed on an
//  "AS-IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. Refer to the
//  License for the specific language governing permissions and limitations.
//
//  Code Modification History:
//  ----------------------------------------------------------------------------------------------------
//  07/18/2018 - J. Ritchie Carroll
//       Generated original version of source code.
//
//******************************************************************************************************

using GSF;
using GSF.Data;
using GSF.Data.Model;
using GSF.Diagnostics;
using GSF.Parsing;
using GSF.Threading;
using GSF.TimeSeries;
using GSF.TimeSeries.Adapters;
using openPDC.Model;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Text;
using System.Timers;
using AlarmStateRecord = openPDC.Model.AlarmState;
using ConnectionStringParser = GSF.Configuration.ConnectionStringParser<GSF.TimeSeries.Adapters.ConnectionStringParameterAttribute>;

// ReSharper disable UnusedMember.Global
// ReSharper disable InheritdocConsiderUsage
namespace openPDC.Adapters
{
    /// <summary>
    /// Represents an adapter that will monitor and report device alarm states.
    /// </summary>
    [Description("Device Alarm State: Monitors and updates alarm states for devices")]
    public class DeviceAlarmStateAdapter : FacileActionAdapterBase
    {
        #region [ Members ]

        private enum AlarmState
        {
            Good,           // Everything is kosher
            Alarm,          // Not available for longer than configured alarm time
            NotAvailable,   // Data is missing or timestamp is outside lead/lag time range
            BadData,        // Quality flags report bad data
            BadTime,        // Quality flags report bad time
            OutOfService    // Source device is disabled
        }

        // Constants

        /// <summary>
        /// Defines the default value for the <see cref="MonitoringRate"/>.
        /// </summary>
        public const int DefaultMonitoringRate = 30000;

        /// <summary>
        /// Defines the default value for the <see cref="AlarmMinutes"/>.
        /// </summary>
        public const double DefaultAlarmMinutes = 10.0D;

        // Fields
        private Timer m_monitoringTimer;
        private ShortSynchronizedOperation m_monitoringOperation;
        private Dictionary<AlarmState, AlarmStateRecord> m_alarmStates;
        private Dictionary<int, MeasurementKey> m_deviceMeasurementKeys;
        private Dictionary<int, DataRow> m_deviceMetadata;
        private Dictionary<Guid, Ticks> m_lastDeviceDataUpdates;
        private Dictionary<Guid, Ticks> m_lastDeviceStateChange;
        private Dictionary<AlarmState, string> m_mappedAlarmStates;
        private Dictionary<AlarmState, int> m_stateCounts;
        private object m_stateCountLock;
        private Ticks m_alarmTime;
        private long m_alarmStateUpdates;
        private long m_externalDatabaseUpdates;
        private bool m_disposed;

        #endregion

        #region [ Constructors ]

        /// <summary>
        /// Creates a new <see cref="DeviceAlarmStateAdapter"/>.
        /// </summary>
        public DeviceAlarmStateAdapter()
        {
            m_alarmTime = TimeSpan.FromMinutes(DefaultAlarmMinutes).Ticks;
        }

        #endregion

        #region [ Properties ]

        /// <summary>
        /// Gets or sets monitoring rate, in milliseconds, for devices.
        /// </summary>
        [ConnectionStringParameter]
        [Description("Defines overall monitoring rate, in milliseconds, for devices.")]
        [DefaultValue(DefaultMonitoringRate)]
        public int MonitoringRate
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the time, in minutes, for which to change the device state to alarm when no data is received.
        /// </summary>
        [ConnectionStringParameter]
        [Description("Defines the time, in minutes, for which to change the device state to alarm when no data is received.")]
        [DefaultValue(DefaultAlarmMinutes)]
        public double AlarmMinutes
        {
            get => m_alarmTime.ToMinutes();
            set => m_alarmTime = TimeSpan.FromMinutes(value).Ticks;
        }

        /// <summary>
        /// Gets or sets the external database connection string used for synchronization of alarm states.
        /// </summary>
        [ConnectionStringParameter]
        [Description("Defines the external database connection string used for synchronization of alarm states.")]
        [DefaultValue("")]
        public string ExternalDatabaseConnnectionString
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the external database provider string used for synchronization of alarm states.
        /// </summary>
        [ConnectionStringParameter]
        [Description("Defines the external database provider string used for synchronization of alarm states.")]
        [DefaultValue("AssemblyName={System.Data, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089}; ConnectionType=System.Data.SqlClient.SqlConnection; AdapterType=System.Data.SqlClient.SqlDataAdapter")]
        public string ExternalDatabaseProviderString
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the external database command used for synchronization of alarm states.
        /// </summary>
        [ConnectionStringParameter]
        [Description("Defines the external database command used for synchronization of alarm states.")]
        [DefaultValue("sp_LogSsamEvent")]
        public string ExternalDatabaseCommand
        {
            get;
            set;
        }
        
        /// <summary>
        /// Gets or sets the external database command parameters with value substitutions used for synchronization of alarm states.
        /// </summary>
        [ConnectionStringParameter]
        [Description("Defines the external database command parameters with value substitutions used for synchronization of alarm states.")]
        [DefaultValue("{MappedAlarmState},1,'PDC_DEVICE_{Acronym}','','openPDC device {Acronym} state = {AlarmState}',''")]
        public string ExternalDatabaseCommandParameters
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the external database mapped alarm states defining the {MappedAlarmState} command parameter substitution parameter used for synchronization of alarm states.
        /// </summary>
        [ConnectionStringParameter]
        [Description("Defines the external database mapped alarm states defining the {MappedAlarmState} command parameter substitution parameter used for synchronization of alarm states.")]
        [DefaultValue("Good=1,Alarm=3,NotAvailable=2,BadData=4,BadTime=4,OutOfService=5")]
        public string ExternalDatabaseMappedAlarmStates
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the flag that determines if external database should report a single composite state or a state for each device.
        /// </summary>
        [ConnectionStringParameter]
        [Description("Defines the flag that determines if external database should report a single composite state or a state for each device.")]
        [DefaultValue(false)]
        public bool ExternalDatabaseReportSingleCompositeState
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets primary keys of input measurements the adapter expects, if any.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)] // Automatically controlled
        public override MeasurementKey[] InputMeasurementKeys
        {
            get => base.InputMeasurementKeys;
            set => base.InputMeasurementKeys = value;
        }

        /// <summary>
        /// Gets or sets output measurements that the adapter will produce, if any.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)] // Automatically controlled
        public override IMeasurement[] OutputMeasurements
        {
            get => base.OutputMeasurements;
            set => base.OutputMeasurements = value;
        }

        /// <summary>
        /// Gets the flag indicating if this adapter supports temporal processing.
        /// </summary>
        public override bool SupportsTemporalProcessing => false;

        /// <summary>
        /// Returns the detailed status of the data input source.
        /// </summary>
        public override string Status
        {
            get
            {
                StringBuilder status = new StringBuilder();

                status.Append(base.Status);
                status.AppendFormat("           Monitoring Rate: {0:N0}ms", MonitoringRate);
                status.AppendLine();
                status.AppendFormat("        Monitoring Enabled: {0}", MonitoringEnabled);
                status.AppendLine();
                status.AppendFormat("    Monitored Device Count: {0:N0}", InputMeasurementKeys?.Length ?? 0);
                status.AppendLine();
                status.AppendFormat("     No Data Alarm Timeout: {0}", m_alarmTime.ToElapsedTimeString(2));
                status.AppendLine();
                status.AppendFormat("       Alarm State Updates: {0:N0}", m_alarmStateUpdates);
                status.AppendLine();
                status.AppendFormat(" External Database Updates: {0:N0}", m_externalDatabaseUpdates);
                status.AppendLine();

                lock (m_stateCountLock)
                {
                    status.AppendFormat("              Good Devices: {0:N0}", m_stateCounts[AlarmState.Good]);
                    status.AppendLine();
                    status.AppendFormat("           Alarmed Devices: {0:N0}", m_stateCounts[AlarmState.Alarm]);
                    status.AppendLine();
                    status.AppendFormat("       Unavailable Devices: {0:N0}", m_stateCounts[AlarmState.NotAvailable]);
                    status.AppendLine();
                    status.AppendFormat("Devices Reporting Bad Data: {0:N0}", m_stateCounts[AlarmState.BadData]);
                    status.AppendLine();
                    status.AppendFormat("Devices Reporting Bad Time: {0:N0}", m_stateCounts[AlarmState.BadTime]);
                    status.AppendLine();
                    status.AppendFormat("    Out of Service Devices: {0:N0}", m_stateCounts[AlarmState.OutOfService]);
                    status.AppendLine();
                }

                return status.ToString();
            }
        }

        private bool MonitoringEnabled => Enabled && (m_monitoringTimer?.Enabled ?? false);

        #endregion

        #region [ Methods ]

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="DeviceAlarmStateAdapter"/> object and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            if (!m_disposed)
            {
                try
                {
                    if (disposing)
                    {
                        if ((object)m_monitoringTimer != null)
                        {
                            m_monitoringTimer.Enabled = false;
                            m_monitoringTimer.Elapsed -= MonitoringTimer_Elapsed;
                            m_monitoringTimer.Dispose();
                        }
                    }
                }
                finally
                {
                    m_disposed = true;          // Prevent duplicate dispose.
                    base.Dispose(disposing);    // Call base class Dispose().
                }
            }
        }

        /// <summary>
        /// Initializes <see cref="DeviceAlarmStateAdapter" />.
        /// </summary>
        public override void Initialize()
        {
            base.Initialize();

            ConnectionStringParser parser = new ConnectionStringParser();
            parser.ParseConnectionString(ConnectionString, this);

            m_alarmStates = new Dictionary<AlarmState, AlarmStateRecord>();
            m_deviceMeasurementKeys = new Dictionary<int, MeasurementKey>();
            m_deviceMetadata = new Dictionary<int, DataRow>();
            m_lastDeviceDataUpdates = new Dictionary<Guid, Ticks>();
            m_lastDeviceStateChange = new Dictionary<Guid, Ticks>();
            m_mappedAlarmStates = new Dictionary<AlarmState, string>();
            m_stateCounts = CreateNewStateCountsMap();
            m_stateCountLock = new object();

            using (AdoDataConnection connection = new AdoDataConnection("systemSettings"))
            {
                // Load alarm state map - this defines database state ID and custom color for each alarm state
                TableOperations<AlarmStateRecord> alarmStateTable = new TableOperations<AlarmStateRecord>(connection);
                AlarmStateRecord[] alarmStateRecords = alarmStateTable.QueryRecords().ToArray();

                foreach (AlarmState alarmState in Enum.GetValues(typeof(AlarmState)))
                {
                    AlarmStateRecord alarmStateRecord = alarmStateRecords.FirstOrDefault(record => record.State.RemoveWhiteSpace().Equals(alarmState.ToString(), StringComparison.OrdinalIgnoreCase));

                    if (alarmStateRecord == null)
                    {
                        alarmStateRecord = alarmStateTable.NewRecord();
                        alarmStateRecord.State = alarmState.ToString();
                        alarmStateRecord.Color = "white";
                    }

                    m_alarmStates[alarmState] = alarmStateRecord;
                }

                // Load any newly defined devices into the alarm device table
                TableOperations<AlarmDevice> alarmDeviceTable = new TableOperations<AlarmDevice>(connection);
                DataRow[] newDevices = connection.RetrieveData("SELECT * FROM Device WHERE IsConcentrator = 0 AND  NOT ID IN (SELECT DeviceID FROM AlarmDevice)").Select();

                foreach (DataRow newDevice in newDevices)
                {
                    AlarmDevice alarmDevice = alarmDeviceTable.NewRecord();

                    bool enabled = newDevice["Enabled"].ToString().ParseBoolean();

                    alarmDevice.DeviceID = newDevice.Field<int>("ID");
                    alarmDevice.StateID = enabled ? m_alarmStates[AlarmState.NotAvailable].ID : m_alarmStates[AlarmState.OutOfService].ID;
                    alarmDevice.DisplayData = enabled ? "0" : GetOutOfServiceTime(newDevice);

                    alarmDeviceTable.AddNewRecord(alarmDevice);

                    // Foreign key relationship with Device table with delete cascade should ensure automatic removals
                }

                List<MeasurementKey> inputMeasurementKeys = new List<MeasurementKey>();

                // Load measurement signal ID to alarm device map
                foreach (AlarmDevice alarmDevice in alarmDeviceTable.QueryRecords())
                {
                    MeasurementKey[] keys = null;
                    DataRow metadata = connection.RetrieveRow("SELECT * FROM Device WHERE ID = {0}", alarmDevice.DeviceID);

                    if ((object)metadata != null)
                    {
                        // Querying from MeasurementDetail because we also want to include disabled device measurements
                        DataTable table = connection.RetrieveData("SELECT SignalID, ID FROM MeasurementDetail WHERE DeviceAcronym = {0} AND SignalAcronym = 'FREQ'", metadata.Field<string>("Acronym"));
                        
                        // ReSharper disable once AccessToDisposedClosure
                        keys = table.AsEnumerable().Select(row => MeasurementKey.LookUpOrCreate(connection.Guid(row, "SignalID"), row["ID"].ToString())).ToArray();
                    }

                    if (keys?.Length > 0)
                    {
                        // Only one frequency is expected
                        MeasurementKey key = keys[0];
                        inputMeasurementKeys.Add(key);
                        m_deviceMeasurementKeys[alarmDevice.DeviceID] = key;
                        m_deviceMetadata[alarmDevice.DeviceID] = metadata;
                        m_lastDeviceDataUpdates[key.SignalID] = DateTime.UtcNow.Ticks;
                        m_lastDeviceStateChange[key.SignalID] = DateTime.UtcNow.Ticks;
                    }
                    else
                    {
                        // Mark alarm record as unavailable if no frequency measurement is available for device
                        alarmDevice.StateID = m_alarmStates[AlarmState.NotAvailable].ID;
                        alarmDevice.DisplayData = GetOutOfServiceTime(metadata);
                        alarmDeviceTable.UpdateRecord(alarmDevice);
                    }
                }

                // Load desired input measurements
                InputMeasurementKeys = inputMeasurementKeys.ToArray();
                TrackLatestMeasurements = true;
            }

            // Parse external database mapped alarm states, if defined
            if (!string.IsNullOrEmpty(ExternalDatabaseMappedAlarmStates))
            {
                string[] mappings = ExternalDatabaseMappedAlarmStates.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string mapping in mappings)
                {
                    string[] parts = mapping.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length == 2 && Enum.TryParse(parts[0].Trim(), out AlarmState state))
                        m_mappedAlarmStates[state] = parts[1].Trim();
                }
            }

            // Define synchronized monitoring operation
            m_monitoringOperation = new ShortSynchronizedOperation(MonitoringOperation, exception => OnProcessException(MessageLevel.Warning, exception));

            // Define monitoring timer
            m_monitoringTimer = new Timer(MonitoringRate)
            {
                AutoReset = true
            };

            m_monitoringTimer.Elapsed += MonitoringTimer_Elapsed;
            m_monitoringTimer.Enabled = true;
        }

        /// <summary>
        /// Queues monitoring operation to update alarm state for immediate execution.
        /// </summary>
        [AdapterCommand("Queues monitoring operation to update alarm state for immediate execution.", "Administrator", "Editor")]
        public void QueueStateUpdate() => m_monitoringOperation?.RunOnceAsync();

        /// <summary>
        /// Gets a short one-line status of this adapter.
        /// </summary>
        /// <param name="maxLength">Maximum number of available characters for display.</param>
        /// <returns>A short one-line summary of the current status of this adapter.</returns>
        public override string GetShortStatus(int maxLength)
        {
            if (MonitoringEnabled)
                return $"Monitoring enabled for every {Ticks.FromMilliseconds(MonitoringRate).ToElapsedTimeString()}".CenterText(maxLength);

            return "Monitoring is disabled...".CenterText(maxLength);            
        }

        private void MonitoringOperation()
        {
            ImmediateMeasurements measurements = LatestMeasurements;
            List<AlarmDevice> alarmDeviceUpdates = new List<AlarmDevice>();
            Dictionary<AlarmState, int> stateCounts = CreateNewStateCountsMap();

            OnStatusMessage(MessageLevel.Info, "Updating device alarm states");

            using (AdoDataConnection connection = new AdoDataConnection("systemSettings"))
            {
                TableOperations<AlarmDevice> alarmDeviceTable = new TableOperations<AlarmDevice>(connection);

                foreach (AlarmDevice alarmDevice in alarmDeviceTable.QueryRecords())
                {
                    if (!m_deviceMeasurementKeys.TryGetValue(alarmDevice.DeviceID, out MeasurementKey key) || 
                        !m_lastDeviceDataUpdates.TryGetValue(key.SignalID, out Ticks lastUpdateTime) || 
                        !m_deviceMetadata.TryGetValue(alarmDevice.DeviceID, out DataRow metadata))
                            continue;

                    TemporalMeasurement measurement = measurements.Measurement(key);
                    Ticks currentTime = DateTime.UtcNow.Ticks;
                    AlarmState state = AlarmState.Good;

                    // Determine and update state
                    if (metadata["Enabled"].ToString().ParseBoolean())
                    {
                        if (double.IsNaN(measurement.AdjustedValue) || measurement.AdjustedValue == 0.0D)
                        {
                            // Value is missing for longer than defined adapter lead / lag time tolerances,
                            // state is unavailable or in alarm if unavailable beyond configured alarm time
                            state = currentTime - lastUpdateTime > m_alarmTime ? AlarmState.Alarm : AlarmState.NotAvailable;
                        }
                        else
                        {
                            // Have a value, update last device data time
                            m_lastDeviceDataUpdates[key.SignalID] = currentTime;

                            if (!measurement.ValueQualityIsGood())
                                state = AlarmState.BadData;
                            else if (!measurement.TimestampQualityIsGood())
                                state = AlarmState.BadTime;
                        }
                    }
                    else
                    {
                        state = AlarmState.OutOfService;
                    }

                    // Track current state counts
                    stateCounts[state]++;

                    // Set alarm device state
                    int stateID = m_alarmStates[state].ID;

                    if (stateID != alarmDevice.StateID)
                    {
                        m_lastDeviceStateChange[key.SignalID] = currentTime;
                        alarmDevice.StateID = stateID;
                    }

                    // Update display text to show time since last alarm state change
                    switch (state)
                    {
                        case AlarmState.Good:
                            alarmDevice.DisplayData = "0";
                            break;
                        case AlarmState.OutOfService:
                            alarmDevice.DisplayData = GetOutOfServiceTime(metadata);
                            break;
                        default:
                            alarmDevice.DisplayData = GetShortElapsedTimeString(currentTime - m_lastDeviceStateChange[key.SignalID]);
                            break;
                    }

                    // Update alarm table record
                    alarmDeviceTable.UpdateRecord(alarmDevice);
                    alarmDeviceUpdates.Add(alarmDevice);
                }

                m_alarmStateUpdates++;

                lock (m_stateCountLock)
                    m_stateCounts = stateCounts;
            }

            if (!string.IsNullOrEmpty(ExternalDatabaseConnnectionString))
            {
                TemplatedExpressionParser parameterTemplate = new TemplatedExpressionParser
                {
                    TemplatedExpression = ExternalDatabaseCommandParameters
                };

                AlarmState compositeState = AlarmState.OutOfService;
                DataRow alarmedDeviceMetadata = null;
                Dictionary<string, string> substitutions = new Dictionary<string, string>();

                // Provide state counts as available substitution parameters
                foreach (KeyValuePair<AlarmState, int> stateCount in stateCounts)
                    substitutions[$"{stateCount.Key}StateCount"] = stateCount.Value.ToString();

                using (AdoDataConnection connection = new AdoDataConnection(ExternalDatabaseConnnectionString, ExternalDatabaseProviderString))
                {
                    foreach (AlarmDevice alarmDevice in alarmDeviceUpdates)
                    {
                        if (!m_deviceMetadata.TryGetValue(alarmDevice.DeviceID, out DataRow metadata))
                            continue;

                        AlarmState state = m_alarmStates.First(record => record.Value.ID == alarmDevice.StateID).Key;

                        if (ExternalDatabaseReportSingleCompositeState)
                        {
                            if (state != AlarmState.Good)
                            {
                                // First encountered alarmed device with highest alarm state will be reported as composite state
                                if (compositeState < state)
                                {
                                    compositeState = state;
                                    alarmedDeviceMetadata = metadata;
                                }
                            }
                        }
                        else
                        {
                            // When ExternalDatabaseReportSingleCompositeState is false, reporting state per device
                            ExternalDatabaseReportState(parameterTemplate, connection, state, metadata, substitutions);
                        }
                    }

                    if (ExternalDatabaseReportSingleCompositeState)
                    {
                        if (compositeState == AlarmState.OutOfService)
                            compositeState = AlarmState.Good;

                        ExternalDatabaseReportState(parameterTemplate, connection, compositeState, alarmedDeviceMetadata, substitutions);
                    }
                }

                m_externalDatabaseUpdates++;
            }
        }

        private void ExternalDatabaseReportState(TemplatedExpressionParser parameterTemplate, AdoDataConnection connection, AlarmState state, DataRow metadata, Dictionary<string, string> initialSubstitutions)
        {
            Dictionary<string, string> substitutions = new Dictionary<string, string>(initialSubstitutions)
            {
                ["AlarmState"] = state.ToString(),
                ["AlarmStateValue"] = ((int)state).ToString()
            };

            if (m_mappedAlarmStates.TryGetValue(state, out string mappedValue))
                substitutions["MappedAlarmState"] = mappedValue;
            else
                substitutions["MappedAlarmState"] = "0";

            // Use device metadata columns as possible substitution parameters
            foreach (DataColumn column in metadata.Table.Columns)
                substitutions[column.ColumnName] = metadata[column.ColumnName].ToString();

            List<object> parameters = new List<object>();
            string commandParameters = parameterTemplate.Execute(substitutions);
            string[] splitParameters = commandParameters.Split(',');

            // Do some basic typing on command parameters
            foreach (string splitParameter in splitParameters)
            {
                string parameter = splitParameter.Trim();

                if (parameter.StartsWith("'") && parameter.EndsWith("'"))
                    parameters.Add(parameter.Length > 2 ? parameter.Substring(1, parameter.Length - 2) : "");
                else if (int.TryParse(parameter, out int ival))
                    parameters.Add(ival);
                else if (double.TryParse(parameter, out double dval))
                    parameters.Add(dval);
                else if (bool.TryParse(parameter, out bool bval))
                    parameters.Add(bval);
                else
                    parameters.Add(parameter);
            }

            connection.ExecuteScalar(ExternalDatabaseCommand, parameters);
        }

        private void MonitoringTimer_Elapsed(object sender, ElapsedEventArgs e) => m_monitoringOperation?.RunOnce();

        #endregion

        #region [ Static ]

        private static readonly string[] s_shortTimeNames = { " yr", " yr", " d", " d", " hr", " hr", " m", " m", " s", " s", "< " };

        private static Dictionary<AlarmState, int> CreateNewStateCountsMap() => new Dictionary<AlarmState, int>
        {
            [AlarmState.Good] = 0,
            [AlarmState.Alarm] = 0,
            [AlarmState.NotAvailable] = 0,
            [AlarmState.BadData] = 0,
            [AlarmState.BadTime] = 0,
            [AlarmState.OutOfService] = 0
        };

        private static string GetOutOfServiceTime(DataRow deviceRow)
        {
            if ((object)deviceRow == null)
                return "U/A";

            try
            {
                return GetShortElapsedTimeString(DateTime.UtcNow.Ticks - Convert.ToDateTime(deviceRow["UpdatedOn"]).Ticks);
            }
            catch
            {
                return "U/A";
            }
        }

        public static string GetShortElapsedTimeString(Ticks span)
        {
            double days = span.ToDays();

            if (days > 365.25D)
                span = span.BaselinedTimestamp(BaselineTimeInterval.Year);
            else if (days > 1.0D)
                span = span.BaselinedTimestamp(BaselineTimeInterval.Day);
            else if (span.ToHours() > 1.0D)
                span = span.BaselinedTimestamp(BaselineTimeInterval.Hour);
            else if (span.ToMinutes() > 1.0D)
                span = span.BaselinedTimestamp(BaselineTimeInterval.Minute);
            else if (span.ToSeconds() > 1.0D)
                span = span.BaselinedTimestamp(BaselineTimeInterval.Second);
            else
                return "0";

            string elapsedTimeString = span.ToElapsedTimeString(0, s_shortTimeNames);

            if (elapsedTimeString.Length > 10)
                elapsedTimeString = elapsedTimeString.Substring(0, 10);

            return elapsedTimeString;
        }

        #endregion
    }
}