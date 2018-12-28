using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SpaceEngineers.Game.ModAPI;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using Sandbox.Game;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class Main : MySessionComponentBase {

    private static Main instance;

    private HashSet<Medbay> allMedicalRooms = new HashSet<Medbay>();
    private Logger logger;

    private Dictionary<long, Vector3D> playerLastDiedLocation = new Dictionary<long, Vector3D>();

    public static Main getInstance() {
        return Main.instance;
    }

    public override void Init(MyObjectBuilder_SessionComponent sessionComponent) {

        base.Init(sessionComponent);

        instance = this;

        logger = Logger.getLogger("MedbayCheckpointSystem");
        logger.WriteLine("Initialized");

        deserializePlayerData();

        MyVisualScriptLogicProvider.PlayerDied += PlayerDied;
        MyVisualScriptLogicProvider.PlayerSpawned += PlayerSpawned;
    }

    protected override void UnloadData() {
        base.UnloadData();

        serializePlayerData();

        MyVisualScriptLogicProvider.PlayerDied -= PlayerDied;
        MyVisualScriptLogicProvider.PlayerSpawned -= PlayerSpawned;
        instance = null;

        if (logger == null)
            return;

        logger.WriteLine("Unloaded");
        logger.Close();
    }

    public void PlayerDied(long playerId) {

        IMyPlayer player = Player(playerId);

        if (player == null) {
            logger.WriteLine("Player Died Event: Player #" + playerId + " not found!");
            return;
        }

        Vector3D position = player.GetPosition();

        lock (playerLastDiedLocation) {
            if (playerLastDiedLocation.ContainsKey(playerId))
                playerLastDiedLocation.Remove(playerId);

            playerLastDiedLocation.Add(playerId, position);
        }

        logger.WriteLine("Player Died Event: Player '" + player.DisplayName + "' died at " + position);
    }

    public void PlayerSpawned(long playerId) {

        IMyPlayer player = Player(playerId);

        if (player == null) {
            logger.WriteLine("Player Spawned Event: Player #" + playerId + " not found!");
            return;
        }

        var character = player.Character;
        if (character == null) {
            logger.WriteLine("Player Spawned Event: Player '" + player.DisplayName + "' has no Character!");
            return;
        }

        if (!playerLastDiedLocation.ContainsKey(playerId)) {
            logger.WriteLine("Player Spawned Event: Player '" + player.DisplayName + "' has no last Died Location!");
            return;
        }

        Vector3D lastDiedPositon = playerLastDiedLocation[playerId];

        double shortestDistance = 0;
        IMyMedicalRoom nearestMedbay = null;

        lock (allMedicalRooms) {
            foreach (Medbay medbay in allMedicalRooms) {

                IMyEntity entity = medbay.Entity;
                Vector3D medbayPosition = entity.GetPosition();

                IMyMedicalRoom medicalRoom = entity as IMyMedicalRoom;

                /* Wir ignorieren Medbays vom Independent Survival Mod */
                SerializableDefinitionId definiton = medicalRoom.BlockDefinition;
                if (definiton.SubtypeId.ToString() == "CythonSuitMedicalRoom")
                    continue;

                /* Wenn nicht Funktional, Nicht Working, Nicht Enabled, oder nicht Powered continue */
                if (!medicalRoom.IsFunctional || !medicalRoom.IsWorking || !medicalRoom.Enabled)
                    continue;

                MyRelationsBetweenPlayerAndBlock relation = medicalRoom.GetUserRelationToOwner(playerId);

                /* Fremde Medbays sind nicht erlaubt. No Owner, Owner und Faction share ist fein. Enemies und Neutral definitiv nicht.  */
                if (relation == MyRelationsBetweenPlayerAndBlock.Enemies
                    || relation == MyRelationsBetweenPlayerAndBlock.Neutral)
                    continue;

                double distance = Vector3D.Distance(medbayPosition, lastDiedPositon);

                if (nearestMedbay == null || distance < shortestDistance) {
                    nearestMedbay = medicalRoom;
                    shortestDistance = distance;
                }
            }
        }

        if (nearestMedbay == null) {
            logger.WriteLine("Player Spawned Event: Player '" + player.DisplayName + "' has usable Medbay nearby!");
            return;
        }

        MatrixD matrix = GetSpawnPosition(nearestMedbay);

        lock (playerLastDiedLocation) {
            playerLastDiedLocation.Remove(playerId);
            playerLastDiedLocation.Add(playerId, matrix.Translation);
        }

        Vector3 velocity = nearestMedbay.CubeGrid.Physics.GetVelocityAtPoint(matrix.Translation);

        character.Physics.LinearVelocity = velocity;
        character.SetWorldMatrix(matrix);

        bool needsDamping = character.Physics.Speed == 0;

        if ((!character.EnabledDamping && needsDamping)
            || (character.EnabledDamping && !needsDamping))
            character.SwitchDamping();


        MyVisualScriptLogicProvider.ShowNotification("You got teleported to the Medical Room nearest to the location you died at.", 5000, MyFontEnum.Green, playerId);
        logger.WriteLine("Player Spawned Event: Player '" + player.DisplayName + "' spawned at Position " + matrix.Translation + "!");

        serializePlayerData();
    }

    public MatrixD GetSpawnPosition(IMyMedicalRoom myMedicalRoom) {

        var model = myMedicalRoom.Model;

        Dictionary<String, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();
        model.GetDummies(dummies);

        IMyModelDummy myModelDummy;

        /* Try find the detectorRespawnDummy */
        if (!dummies.TryGetValue("dummy detector_respawn", out myModelDummy))
            if (!dummies.TryGetValue("detector_respawn", out myModelDummy))
                myModelDummy = null;

        if (myModelDummy != null)
            return MatrixD.Multiply(MatrixD.CreateTranslation(myModelDummy.Matrix.Translation), myMedicalRoom.WorldMatrix);

        logger.WriteLine("GetSpawnPosition: Couldnt Find Dummies fallback to World-Matrix!");

        MatrixD matrix = myMedicalRoom.WorldMatrix;

        Vector3D positon = myMedicalRoom.Position;

        positon += matrix.Forward;
        positon += matrix.Down;
        positon += matrix.Right;

        matrix.Translation = positon;

        return matrix;
    }

    public void serializePlayerData() {

        StringBuilder sb = new StringBuilder();

        if (playerLastDiedLocation == null)
            return;

        lock (playerLastDiedLocation) {
            foreach (long playerId in playerLastDiedLocation.Keys) {
                Vector3D position = playerLastDiedLocation[playerId];

                sb.Append(playerId);
                sb.Append(",");
                sb.Append(position.X.ToString("#0.0"));
                sb.Append(",");
                sb.Append(position.Y.ToString("#0.0"));
                sb.Append(",");
                sb.Append(position.Z.ToString("#0.0"));
                sb.Append(";");
            }
        }

        MyAPIGateway.Utilities.SetVariable("mod_1407133818_playerLastDiedLocation", sb.ToString());

        if (logger == null)
            return;

        logger.WriteLine("deserializePlayerData: playerdata saved!");
    }

    public void deserializePlayerData() {

        String settings;

        MyAPIGateway.Utilities.GetVariable("mod_1407133818_playerLastDiedLocation", out settings);

        if (settings == null) {
            logger.WriteLine("deserializePlayerData: No saved playerdata found!");
            return;
        }

        String[] playerDataArray = settings.Split(new String[] { ";" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (String playerData in playerDataArray) {
            String[] splitParts = playerData.Split(new String[] { "," }, StringSplitOptions.None);

            long playerId;
            double x;
            double y;
            double z;

            if (!long.TryParse(splitParts[0], out playerId))
                continue;

            if (!double.TryParse(splitParts[1], out x))
                continue;

            if (!double.TryParse(splitParts[2], out y))
                continue;


            if (!double.TryParse(splitParts[3], out z))
                continue;

            playerLastDiedLocation.Add(playerId, new Vector3D(x, y, z));

            logger.WriteLine("deserializePlayerData: Loaded playerdata: " + playerId + ", " + x + ", " + y + ", " + z + "!");
        }

        logger.WriteLine("deserializePlayerData: playerdata loaded!");
    }

    public void addMedicalRoom(Medbay medicalRoom) {

        lock (allMedicalRooms) {
            allMedicalRooms.Add(medicalRoom);
            logger.WriteLine("Medical Room Added Event: Medical Room " + medicalRoom + " added!");
        }
    }

    public void removeMedicalRoom(Medbay medicalRoom) {

        lock (allMedicalRooms) {
            allMedicalRooms.Remove(medicalRoom);
            logger.WriteLine("Medical Room Removed Event: Medical Room " + medicalRoom + " removed!");
        }
    }

    private IMyPlayer Player(long entityId) {
        try {
            var playerList = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(playerList, p => p != null && p.DisplayName != "" && p.IdentityId == entityId);

            return playerList.FirstOrDefault();
        } catch (Exception e) {
            logger.WriteLine("Error on getting Player Identity " + e);
            return null;
        }
    }
}

[MyEntityComponentDescriptor(typeof(MyObjectBuilder_MedicalRoom), useEntityUpdate: false)]
public class Medbay : MyGameLogicComponent {

    public override void Init(MyObjectBuilder_EntityBase objectBuilder) {

        base.Init(objectBuilder);

        NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME;
    }

    public override void UpdateAfterSimulation() {
        NeedsUpdate = MyEntityUpdateEnum.NONE;
        Main.getInstance().addMedicalRoom(this);
    }

    public override void Close() {
        base.Close();
        Main.getInstance().removeMedicalRoom(this);
    }

    public override string ToString() {
        return this.Entity.ToString();
    }
}
