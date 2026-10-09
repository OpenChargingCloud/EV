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

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.EV
{

    /// <summary>
    /// What a vehicle adds to the resources every node has, and the roles of
    /// the people around it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The node brings the viewer, who may look at everything, and the
    /// administrators, who may do everything - certificates included, which is
    /// the one resource nobody else may edit: somebody who can add a root can
    /// make this vehicle believe a station nobody else would, and the
    /// credentials are the whole of who it is and who pays for what it takes.
    /// </para>
    /// <para>
    /// The configuration file may add roles to these and say differently what
    /// one of them may do - see the node's "roles" section. What is written
    /// here is what a vehicle is when its file says nothing.
    /// </para>
    /// </remarks>
    public static class VehicleAccess
    {

        #region Resources

        /// <summary>
        /// What the vehicle says it is and what its battery wants: the
        /// capacity, the power, and when it is leaving.
        /// </summary>
        public const String  Vehicle   = "vehicle";

        /// <summary>
        /// The wire below the charging cable: which interface and protocol,
        /// the SLAC peer, the 10BASE-T1S bus - and, run, a discovery or a
        /// pairing to find out whether a station answers.
        /// </summary>
        public const String  V2G       = "v2g";

        /// <summary>
        /// What a charging session asks for once it runs - and, run, a session
        /// itself, which draws power, or occupies an outlet somebody else is
        /// queueing for.
        /// </summary>
        public const String  Session   = "session";

        /// <summary>
        /// All three.
        /// </summary>
        public static readonly IReadOnlyList<String>  Resources = [ Vehicle, V2G, Session ];

        #endregion

        #region Roles

        /// <summary>
        /// Whoever drives this vehicle: may say what the battery wants and when
        /// the car is leaving, may plug it in, and may ask whether the network
        /// and the link work - but may not repoint it at other name and time
        /// servers or another link, and may not touch the certificates it is
        /// known by.
        /// </summary>
        /// <remarks>
        /// Reads every resource but the SSH server's, each named: what that
        /// page shows - every account's keys, who is signed in over SSH right
        /// now - is the administrators' and the garage's business, not the
        /// driver's. Named rather than "everything", so a resource a new node
        /// brings is read by the driver once somebody says so here.
        /// </remarks>
        public static readonly Role  Driver   = new ("driver",
                                                     [ Permission.Read(NodeResources.Configuration),
                                                       Permission.Read(NodeResources.DNS),
                                                       Permission.Read(NodeResources.NTS),
                                                       Permission.Read(NodeResources.Certificates),
                                                       Permission.Read(Vehicle),
                                                       Permission.Read(V2G),
                                                       Permission.Read(Session),
                                                       Permission.Run (NodeResources.DNS),
                                                       Permission.Run (NodeResources.NTS),
                                                       Permission.Run (V2G),
                                                       Permission.Edit(Vehicle),
                                                       Permission.Edit(Session),
                                                       Permission.Run (Session) ],
                                                     "drives the vehicle: its battery, its sessions, and asking whether things work");

        /// <summary>
        /// Whoever commissions this vehicle: everything the driver may do, and
        /// on top of it where it resolves names, where it reads the time and
        /// how it reaches a station.
        /// </summary>
        /// <remarks>
        /// The network settings sit here rather than with the driver because a
        /// vehicle that cannot resolve a name fails in a way that reads like a
        /// broken station, and working that out is a garage's job.
        /// </remarks>
        public static readonly Role  Service  = new ("service",
                                                     [ Permission.Read(Permission.AnyResource),
                                                       Permission.Edit(NodeResources.DNS),
                                                       Permission.Run (NodeResources.DNS),
                                                       Permission.Edit(NodeResources.NTS),
                                                       Permission.Run (NodeResources.NTS),
                                                       Permission.Edit(V2G),
                                                       Permission.Run (V2G),
                                                       Permission.Edit(Vehicle),
                                                       Permission.Edit(Session),
                                                       Permission.Run (Session) ],
                                                     "commissions the vehicle: everything the driver may do, and its network and link");

        /// <summary>
        /// Both, in the order a sentence naming them reads best.
        /// </summary>
        public static readonly IReadOnlyList<Role>  Roles = [ Driver, Service ];

        #endregion

    }

}
