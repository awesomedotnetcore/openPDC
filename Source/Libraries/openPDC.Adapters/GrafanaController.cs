﻿//******************************************************************************************************
//  GrafanaController.cs - Gbtc
//
//  Copyright © 2016, Grid Protection Alliance.  All Rights Reserved.
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
//  09/15/2016 - Ritchie Carroll
//       Generated original version of source code.
//
//******************************************************************************************************

using GrafanaAdapters;
using GSF.Data;
using GSF.Data.Model;
using openPDC.Model;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using CancellationToken = System.Threading.CancellationToken;
using ValidateAntiForgeryTokenAttribute = System.Web.Mvc.ValidateAntiForgeryTokenAttribute;

namespace openPDC.Adapters
{
    /// <summary>
    /// Represents a REST based API for a simple JSON based Grafana data source.
    /// </summary>
    public class GrafanaController : ApiController
    {
        /// <summary>
        /// Validates that openHistorian Grafana data source is responding as expected.
        /// </summary>
        [HttpGet]
        public HttpResponseMessage Index()
        {
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        /// <summary>
        /// Queries current alarm device state.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IEnumerable<AlarmDeviceStateView>> GetAlarmState(QueryRequest request, CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew(() =>
            {
                using (AdoDataConnection connection = new AdoDataConnection("systemSettings"))
                {
                    return new TableOperations<AlarmDeviceStateView>(connection).QueryRecords();
                }
            },
            cancellationToken);
        }

        /// <summary>
        /// Queries current data availability.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IEnumerable<DataAvailability>> GetDataAvailability(QueryRequest request, CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew(() =>
            {
                using (AdoDataConnection connection = new AdoDataConnection("systemSettings"))
                {
                    return new TableOperations<DataAvailability>(connection).QueryRecords();
                }
            },
            cancellationToken);
        }


        /// <summary>
        /// Handled query function requests - returns empty results.
        /// </summary>
        /// <param name="request">Query request.</param>
        /// <param name="cancellationToken">Propagates notification from client that operations should be canceled.</param>
        [HttpPost]
        [SuppressMessage("Security", "SG0016", Justification = "Current operation dictated by Grafana. CSRF exposure limited to data access.")]
        public virtual Task<List<TimeSeriesValues>> Query(QueryRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new List<TimeSeriesValues>());
        }

    }
}
