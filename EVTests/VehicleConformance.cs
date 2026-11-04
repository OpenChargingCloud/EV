/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of EV <https://github.com/OpenChargingCloud/EV>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.EV.Tests
{

    /// <summary>
    /// What every node has to answer, asked of a vehicle - the conformance
    /// suite of WWCP_Node_TestKit.
    /// </summary>
    /// <remarks>
    /// The sign-in, the configuration, name resolution and the time servers,
    /// the diagnostics, the log and its event stream, stopping with browsers
    /// watching, the certificate store, the web interface and the files
    /// over SFTP. This suite had copies of the event stream's tests and of
    /// the learned pins, and none of the rest.
    /// </remarks>
    public partial class VehicleConformance : NodeConformanceTests
    {

        protected override WWCPNode NewNode(String   Directory,
                                            JObject  Configuration)

            => New(Directory, Configuration, LogPath: null);

        // The SFTP cases that read /logs, which a vehicle writing no log
        // files would leave inconclusive.
        protected override WWCPNode NewNodeWritingLogFiles(String   Directory,
                                                           JObject  Configuration,
                                                           String   LogPath)

            => New(Directory, Configuration, LogPath);

        private static EV New(String   Directory,
                              JObject  Configuration,
                              String?  LogPath)
        {

            var file = Path.Combine(Directory, WWCPConfigFile.DefaultFileName);

            File.WriteAllText(file, Configuration.ToString());

            return new EV(
                       HTTPPort:          IPPort.Parse(TestPorts.Free()),
                       AccountsPath:      Path.Combine(Directory, "accounts"),
                       ConfigFile:        new WWCPConfigFile(file),
                       CertificatesPath:  Path.Combine(Directory, "certificates"),
                       LogToConsole:      false,
                       LogPath:           LogPath,
                       BridgeDebugLog:    false
                   );

        }

    }

}
