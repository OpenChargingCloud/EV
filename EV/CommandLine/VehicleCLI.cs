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

using System.Reflection;

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;

#endregion

namespace cloud.charging.open.EV.CommandLine
{

    /// <summary>
    /// The command line of a running vehicle.
    /// </summary>
    /// <remarks>
    /// Everything a command needs is reachable from here, which is why every
    /// command of the vehicle's takes one of these: the vehicle itself, and
    /// through it its configuration, its log and everything the JSON API can
    /// do. A command is a third way of asking for the same thing, beside the
    /// web interface and the switches at a start - never an implementation of
    /// its own.
    ///
    /// The node's command line, with the commands every node has - syncNTS
    /// among them - and the console until 'quit', Ctrl+C or SIGTERM. Commands
    /// are not listed anywhere: what only a vehicle can be told is anything in
    /// this assembly that implements ICLICommand and can be built from a
    /// VehicleCLI, found as the node's are, so a new command is a new file and
    /// nothing else.
    ///
    /// Here, beside the vehicle, and not in the program that runs it: what a
    /// vehicle can be told is the vehicle's, and is told over SSH as well as
    /// at the console. The vehicle hands it to whoever signs in over SSH by
    /// itself (see EV.cs), so that a vehicle run by another program - a test -
    /// has the same commands as one run by EVCLI. A program with commands of
    /// its own derives from this one and sets WWCPNode.CommandLines anew.
    /// </remarks>
    public class VehicleCLI : NodeCLI
    {

        #region Properties

        /// <summary>
        /// The vehicle these commands are about.
        /// </summary>
        public EV Vehicle { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create the command line of the given vehicle.
        /// </summary>
        /// <param name="Vehicle">The running vehicle.</param>
        /// <param name="AssembliesWithCLICommands">Further assemblies to search for commands. This one and the node's are searched either way.</param>
        public VehicleCLI(EV                 Vehicle,
                          params Assembly[]  AssembliesWithCLICommands)

            : base(Vehicle, AssembliesWithCLICommands)

        {

            this.Vehicle = Vehicle;

            RegisterCLIType(typeof(VehicleCLI));

        }

        /// <summary>
        /// Create the command line of the given vehicle on the given terminal,
        /// for the given caller - a session over SSH.
        /// </summary>
        /// <param name="Vehicle">The running vehicle.</param>
        /// <param name="Terminal">What the command line is typed at and written on.</param>
        /// <param name="Caller">Who is typing at it.</param>
        /// <param name="AssembliesWithCLICommands">Further assemblies to search for commands. This one and the node's are searched either way.</param>
        public VehicleCLI(EV                 Vehicle,
                          ICLITerminal       Terminal,
                          CLICaller          Caller,
                          params Assembly[]  AssembliesWithCLICommands)

            : base(Vehicle, Terminal, Caller, AssembliesWithCLICommands)

        {

            this.Vehicle = Vehicle;

            RegisterCLIType(typeof(VehicleCLI));

        }

        #endregion


        #region (protected override) GetPrompt()

        /// <summary>
        /// The vehicle's own name, because a machine that is one of several on a
        /// bench should say which one it is before it asks for a command.
        /// </summary>
        protected override String GetPrompt()

            => $"{Vehicle.VehicleName}> ";

        #endregion

    }

}
