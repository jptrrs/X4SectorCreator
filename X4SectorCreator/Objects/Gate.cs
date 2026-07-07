using System.Text.Json.Serialization;
using X4SectorCreator.Helpers;

namespace X4SectorCreator.Objects
{
    public class Gate : ICloneable
    {
        public int Id { get; set; }
        public string ConnectionName { get; set; }
        public string ParentSectorName { get; set; }
        public string DestinationSectorName { get; set; }
        public string Source { get; set; } // format: c000_s000_z000
        public string Destination { get; set; } // format: c000_s000_z000
        public string SourcePath { get; set; } // format: prefix_c000_connection/prefix_c000_s000_connection/prefix_c000_s000_z000_connection/prefix_g000_source_destination_connection
        public string DestinationPath { get; set; } // format: prefix_c000_connection/prefix_c000_s000_connection/prefix_c000_s000_z000_connection/prefix_g000_source_destination_connection
        public int Yaw { get; set; }
        public int Pitch { get; set; }
        public int Roll { get; set; }
        public GateType Type { get; set; }
        public Point Position { get; set; }
        public bool IsHighwayGate { get; set; }

        /// <summary>
        /// Determines if the Gate is a vanilla gate.
        /// </summary>
        [JsonIgnore]
        public bool IsBaseGame => IsHighwayGate || ConnectionName != null;

        /// <summary>
        /// Used to make a connection with new sectors.
        /// </summary>
        /// <param name="modPrefix"></param>
        /// <param name="cluster"></param>
        /// <param name="sector"></param>
        /// <param name="zone"></param>
        public void SetSourcePath(string modPrefix, Cluster cluster, Sector sector, Zone zone)
        {
            SourcePath = ConvertToPath(modPrefix, cluster, sector, zone, this);
        }

        /// <summary>
        /// Used to make a connection with new sectors.
        /// </summary>
        /// <param name="modPrefix"></param>
        /// <param name="cluster"></param>
        /// <param name="sector"></param>
        /// <param name="zone"></param>
        public void SetDestinationPath(string modPrefix, Cluster cluster, Sector sector, Zone zone, Gate gate)
        {
            DestinationPath = ConvertToPath(modPrefix, cluster, sector, zone, gate);
        }

        private static string ConvertToPath(string modPrefix, Cluster cluster, Sector sector, Zone zone, Gate gate)
        {
            bool isFullBaseGame = cluster.IsBaseGame && sector.IsBaseGame;
            bool isHalfBaseGame = cluster.IsBaseGame && !sector.IsBaseGame;

            string path;
            if (isFullBaseGame)
            {
                string clusterConnection = $"{cluster.BaseGameMapping.CapitalizeFirstLetter()}_connection";
                string sectorConnection = $"{cluster.BaseGameMapping.CapitalizeFirstLetter()}_{sector.BaseGameMapping.CapitalizeFirstLetter()}_connection";
                string zoneConnection = $"{modPrefix}_ZO_{cluster.BaseGameMapping.CapitalizeFirstLetter()}_{sector.BaseGameMapping.CapitalizeFirstLetter()}_z{zone.Id:D3}_connection";
                string gateConnection = $"{modPrefix}_GA_g{gate.Id:D3}_{gate.Source}_{gate.Destination}_connection";
                path = $"{clusterConnection}/{sectorConnection}/{zoneConnection}/{gateConnection}";
            }
            else if (isHalfBaseGame)
            {
                string clusterConnection = $"{cluster.BaseGameMapping.CapitalizeFirstLetter()}_connection";
                string sectorConnection = $"{modPrefix}_SE_{cluster.BaseGameMapping.CapitalizeFirstLetter()}_s{sector.Id:D3}_connection";
                string zoneConnection = $"{modPrefix}_ZO_{cluster.BaseGameMapping.CapitalizeFirstLetter()}_s{sector.Id:D3}_z{zone.Id:D3}_connection";
                string gateConnection = $"{modPrefix}_GA_g{gate.Id:D3}_{gate.Source}_{gate.Destination}_connection";
                path = $"{clusterConnection}/{sectorConnection}/{zoneConnection}/{gateConnection}";
            }
            else
            {
                string clusterConnection = $"{modPrefix}_CL_c{cluster.Id:D3}_connection";
                string sectorConnection = $"{modPrefix}_SE_c{cluster.Id:D3}_s{sector.Id:D3}_connection";
                string zoneConnection = $"{modPrefix}_ZO_c{cluster.Id:D3}_s{sector.Id:D3}_z{zone.Id:D3}_connection";
                string gateConnection = $"{modPrefix}_GA_g{gate.Id:D3}_{gate.Source}_{gate.Destination}_connection";
                path = $"{clusterConnection}/{sectorConnection}/{zoneConnection}/{gateConnection}";
            }

            return path;
        }

        public enum GateType
        {
            // Gate
            props_gates_anc_gate_macro,
            // Accelerator
            props_gates_orb_accelerator_01_macro
        }

        public override string ToString()
        {
            return ParentSectorName;
        }

        public object Clone()
        {
            return new Gate
            {
                Position = Position,
                Id = Id,
                ConnectionName = ConnectionName,
                Destination = Destination,
                DestinationPath = DestinationPath,
                DestinationSectorName = DestinationSectorName,
                IsHighwayGate = IsHighwayGate,
                ParentSectorName = ParentSectorName,
                Pitch = Pitch,
                Roll = Roll,
                Source = Source,
                SourcePath = SourcePath,
                Type = Type,
                Yaw = Yaw
            };
        }

        //A bit of self-awareness
        public Cluster FindDestinationCluster()
        {
            Cluster result;
            FindDestination(out result);
            return result;
        }

        public Sector FindDestination(out Cluster cluster)
        {
            //Replaced LINQ with a foreach loop so we can extract both sector and cluster in one go.
            //Could probably be optimized if we avoided searching for strings, but I'm leaving the criteria as is for now.
            var result = new Sector();
            var Pcluster = new Cluster();
            foreach (var c in MainForm.Instance.AllClusters.Values)
            {
                var Psector = c.Sectors.Find(x => x.Name.Equals(DestinationSectorName, StringComparison.OrdinalIgnoreCase));
                if (Psector != null)
                {
                    Pcluster = c;
                    result = Psector; 
                    break;
                }
            }
            cluster = Pcluster;
            return result;
        }
    }
}
