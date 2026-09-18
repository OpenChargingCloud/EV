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

using System.Diagnostics.CodeAnalysis;

#endregion

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

namespace cloud.charging.open.EV.Web
{

    /// <summary>
    /// What somebody signed in to this vehicle is allowed to do.
    /// </summary>
    /// <remarks>
    /// Flags rather than a list, because a permission is asked about one at a
    /// time and answered by a single test - and because the set a role grants
    /// is then a constant instead of a collection to be built and searched.
    /// </remarks>
    [Flags]
    public enum Permissions : UInt32
    {

        /// <summary>
        /// Nothing at all. What an unknown role would grant.
        /// </summary>
        None                   = 0,

        /// <summary>
        /// See how this vehicle is configured.
        /// </summary>
        ReadConfiguration      = 1,

        /// <summary>
        /// Change how this vehicle reaches the network: its name resolution
        /// and where it reads the time.
        /// </summary>
        /// <remarks>
        /// Separate from the charging settings below because it is reversible
        /// and it complains: a wrong name server makes the vehicle say so, and
        /// the next change puts it right.
        /// </remarks>
        ChangeNetworkSettings  = 2,

        /// <summary>
        /// Make this vehicle ask a name server, a time server or the link
        /// below the charging cable something, to find out whether it can.
        /// </summary>
        /// <remarks>
        /// Its own permission and not part of reading: a diagnostic sends
        /// traffic from this vehicle to a host somebody names - or, in the
        /// case of SDP, to every host on the link at once - which is more than
        /// it sounds like to hand to everybody who may look at a page.
        /// </remarks>
        RunDiagnostics         = 4,

        /// <summary>
        /// Change what this vehicle asks a station for: the battery it says it
        /// has, the power it wants, and when it is leaving.
        /// </summary>
        /// <remarks>
        /// Day-to-day, and the one thing a driver actually changes. Everything
        /// it can be set to is something a station may refuse, so the worst a
        /// wrong figure does is a session that charges slower than it could.
        /// </remarks>
        ChangeChargingSettings = 8,

        /// <summary>
        /// Run a session on the wire below the charging cable: discover a
        /// station, pair over SLAC, charge.
        /// </summary>
        /// <remarks>
        /// Separate from the diagnostics above, which only ask questions. This
        /// one draws power - or, against a station that is not a simulator,
        /// occupies an outlet somebody else is queueing for.
        /// </remarks>
        RunSessions            = 16,

        /// <summary>
        /// Put certificates on this vehicle, and take them off: the Vehicle
        /// certificate it is known by, the contract certificate that pays, and
        /// the OEM provisioning certificate it was born with.
        /// </summary>
        /// <remarks>
        /// The three of them are the whole of who this vehicle is and who pays
        /// for what it takes, which is why this is the one permission not even
        /// the owner gets by default.
        /// </remarks>
        ManageCredentials      = 32

    }


    /// <summary>
    /// A role somebody signs in as: a name, and the permissions it carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A closed set, and deliberately so: a role this vehicle has never heard
    /// of is a role it cannot enforce. So a group whose name is not one of
    /// these grants nothing, rather than quietly granting something - or, far
    /// worse, being taken for a known one because it looks similar.
    /// </para>
    /// <para>
    /// Each role is a user group in the HTTPExt API, under the same name, and
    /// membership of that group is what carries the permissions below. The
    /// permissions stay here because they are this vehicle's own vocabulary:
    /// the HTTPExt API knows users, groups and organizations, and has no
    /// opinion about what "may start a charging session" means. So it answers
    /// who somebody is and this answers what that lets them do.
    /// </para>
    /// </remarks>
    /// <param name="Name">The role, and the name of the user group that carries it.</param>
    /// <param name="Permissions">What it grants.</param>
    public sealed record UserRole(String       Name,
                                  Permissions  Permissions)
    {

        #region Properties

        /// <summary>
        /// The user group in the HTTPExt API whose members hold this role.
        /// </summary>
        public UserGroup_Id  GroupId
            => UserGroup_Id.Parse(Name);

        #endregion


        #region Data

        /// <summary>
        /// May look at this vehicle, and do nothing to it.
        /// </summary>
        public static readonly UserRole  Viewer       = new ("viewer",
                                                             Permissions.ReadConfiguration);

        /// <summary>
        /// Whoever drives this vehicle: may say what the battery wants and
        /// when the car is leaving, may plug it in, and may ask whether the
        /// network works - but may not repoint it at other name and time
        /// servers, and may not touch the certificates it is known by.
        /// </summary>
        public static readonly UserRole  Driver       = new ("driver",
                                                             Permissions.ReadConfiguration      |
                                                             Permissions.RunDiagnostics         |
                                                             Permissions.ChangeChargingSettings |
                                                             Permissions.RunSessions);

        /// <summary>
        /// Whoever commissions this vehicle: everything the driver may do, and
        /// on top of it where it resolves names and reads the time.
        /// </summary>
        /// <remarks>
        /// The network settings sit here rather than with the driver because a
        /// vehicle that cannot resolve a name fails in a way that reads like a
        /// broken station, and working that out is a garage's job.
        /// </remarks>
        public static readonly UserRole  Service      = new ("service",
                                                             Permissions.ReadConfiguration      |
                                                             Permissions.ChangeNetworkSettings  |
                                                             Permissions.RunDiagnostics         |
                                                             Permissions.ChangeChargingSettings |
                                                             Permissions.RunSessions);

        /// <summary>
        /// Everything this vehicle can be told, by whoever is trusted with all
        /// of it at once.
        /// </summary>
        public static readonly UserRole  SystemAdmin  = new ("systemadmin",
                                                             Permissions.ReadConfiguration      |
                                                             Permissions.ChangeNetworkSettings  |
                                                             Permissions.RunDiagnostics         |
                                                             Permissions.ChangeChargingSettings |
                                                             Permissions.RunSessions            |
                                                             Permissions.ManageCredentials);

        /// <summary>
        /// Every role this vehicle knows.
        /// </summary>
        public static readonly IReadOnlyList<UserRole>  All = [ Viewer, Driver, Service, SystemAdmin ];

        #endregion


        #region (static) TryParse(Text, out Role, out Error)

        /// <summary>
        /// A role by the name the login file writes it under, in any case.
        /// </summary>
        public static Boolean TryParse(String?                             Text,
                                       [NotNullWhen(true)]  out UserRole?  Role,
                                       [NotNullWhen(false)] out String?    Error)
        {

            Role   = All.FirstOrDefault(role => String.Equals(role.Name, Text?.Trim(), StringComparison.OrdinalIgnoreCase));

            Error  = Role is null
                         ? $"\"{Text}\" is not a role this vehicle knows. Known roles: {String.Join(", ", All.Select(role => role.Name))}."
                         : null;

            return Role is not null;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()
            => Name;

        #endregion

    }


    /// <summary>
    /// What a set of roles adds up to.
    /// </summary>
    public static class UserRoleExtensions
    {

        #region PermissionsOf(this Roles)

        /// <summary>
        /// Everything the given roles grant together.
        /// </summary>
        public static Permissions PermissionsOf(this IEnumerable<UserRole> Roles)
        {

            var permissions = Permissions.None;

            foreach (var role in Roles)
                permissions |= role.Permissions;

            return permissions;

        }

        #endregion

        #region Names(this Permissions)

        /// <summary>
        /// The permissions as the web interface reads them, so that a page can
        /// grey out what this browser may not do instead of finding out by
        /// being refused.
        /// </summary>
        /// <remarks>
        /// What the browser is told is a copy of what the vehicle enforces, and
        /// not the enforcement: every request is checked again on arrival. A
        /// greyed-out button is a courtesy, not a lock.
        /// </remarks>
        public static IEnumerable<String> Names(this Permissions Permissions)

            => Enum.GetValues<Permissions>().
                    Where (permission => permission != Web.Permissions.None && Permissions.HasFlag(permission)).
                    Select(permission => Char.ToLowerInvariant(permission.ToString()[0]) + permission.ToString()[1..]);

        #endregion

    }

}
