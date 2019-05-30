using System;
using System.Net.Sockets;
using System.Linq;
using System.Collections.Generic;
using System.Threading;

using LunarLabs.Parser.JSON;
using LunarLabs.WebServer.Core;

using Phantasma.Blockchain;
using Phantasma.Cryptography;
using Phantasma.Core.Utils;
using Phantasma.Numerics;
using Phantasma.Core.Types;
using Phantasma.Spook.GUI;
using Phantasma.API;
using Phantasma.Network.P2P;
using Phantasma.Spook.Modules;
using Phantasma.Spook.Plugins;
using Phantasma.Blockchain.Contracts;
using Phantasma.Blockchain.Contracts.Native;
using Phantasma.Blockchain.Utils;
using Phantasma.Blockchain.Plugins;
using Phantasma.CodeGen.Assembler;
using Phantasma.VM.Utils;
using Phantasma.Core;
using Phantasma.Network.P2P.Messages;
using Phantasma.Spook.Nachomen;
using Logger = Phantasma.Core.Log.Logger;
using ConsoleLogger = Phantasma.Core.Log.ConsoleLogger;

namespace Phantasma.Spook
{
    [AttributeUsage(AttributeTargets.Class)]
    public class ModuleAttribute: Attribute
    {
        public readonly string Name;

        public ModuleAttribute(string name)
        {
            Name = name;
        }
    }

    public interface IPlugin
    {
        string Channel { get; }
        void Update();
    }

    public class CLI
    {
        static void Main(string[] args)
        {
            new CLI(args);
        }

        // testnet validators, hardcoded for now
        private static readonly string[] validatorWIFs = new string[]
        {
            "L2LGgkZAdupN2ee8Rs6hpkc65zaGcLbxhbSDGq8oh6umUxxzeW25", //P2f7ZFuj6NfZ76ymNMnG3xRBT5hAMicDrQRHE4S7SoxEr
            "L1sEB8Z6h5Y7aQKqxbAkrzQLY5DodmPacjqSkFPmcR82qdmHEEdY", // PGBinkbZA3Q6BxMnL2HnJSBubNvur3iC6GtQpEThDnvrr
            "KxWUCAD2wECLfA7diT7sV7V3jcxAf9GSKqZy3cvAt79gQLHQ2Qo8", // PDiqQHDwe6MTcP6TH6DYjq7FTUouvy2YEkDXz2chCABCb
        };

        private readonly Node node;
        private readonly Logger logger;
        private readonly Mempool mempool;
        private bool running = false;

        private Nexus nexus;
        private NexusAPI api;

        private ConsoleGUI gui;

        private List<IPlugin> plugins = new List<IPlugin>();

        private static BigInteger FetchBalance(JSONRPC_Client rpc, Logger logger, string host, Address address)
        {
            var response = rpc.SendRequest(logger, host, "getAccount", address.ToString());
            if (response == null)
            {
                logger.Error($"Error fetching balance of {address}...");
                return 0;
            }

            var balances = response["balances"];
            if (balances == null)
            {
                logger.Error($"Error fetching balance of {address}...");
                return 0;
            }

            BigInteger total = 0;

            foreach (var entry in balances.Children)
            {
                var chain = entry.GetString("chain");
                var symbol = entry.GetString("symbol");

                if (symbol == Nexus.FuelTokenSymbol)
                {
                    total += BigInteger.Parse(entry.GetString("amount"));
                }
            }

            return total;
        }

        private static Hash SendTransfer(JSONRPC_Client rpc, Logger logger, string host, KeyPair from, Address to, BigInteger amount)
        {
            Throw.IfNull(rpc, nameof(rpc));
            Throw.IfNull(logger, nameof(logger));

            var script = ScriptUtils.BeginScript().AllowGas(from.Address, Address.Null, 1, 9999).TransferTokens("SOUL", from.Address, to, amount).SpendGas(from.Address).EndScript();

            var tx = new Transaction("simnet", "main", script, Timestamp.Now + TimeSpan.FromMinutes(30));
            tx.Sign(from);

            var bytes = tx.ToByteArray(true);

            //log.Debug("RAW: " + Base16.Encode(bytes));

            var response = rpc.SendRequest(logger, host, "sendRawTransaction", Base16.Encode(bytes));
            if (response == null)
            {
                logger.Error($"Error sending {amount} {Nexus.FuelTokenSymbol} from {from.Address} to {to}...");
                return Hash.Null;
            }

            if (response.HasNode("error"))
            {
                var error = response.GetString("error");
                logger.Error("Error: " + error);
                return Hash.Null;
            }

            var hash = response.Value;
            return Hash.Parse(hash);
        }

        private static bool ConfirmTransaction(JSONRPC_Client rpc, Logger logger, string host, Hash hash, int maxTries = 99999)
        {
            var hashStr = hash.ToString();

            int tryCount = 0;

            int delay = 250;
            do
            {
                var response = rpc.SendRequest(logger, host, "getConfirmations", hashStr);
                if (response == null)
                {
                    logger.Error("Transfer request failed");
                    return false;
                }

                var confirmations = response.GetInt32("confirmations");
                if (confirmations > 0)
                {
                    logger.Success("Confirmations: " + confirmations);
                    return true;
                }

                tryCount--;
                if (tryCount >= maxTries)
                {
                    return false;
                }

                Thread.Sleep(delay);
                delay *= 2;
            } while (true);
        }

        private void SenderSpawn(int ID, KeyPair masterKeys, string host, BigInteger initialAmount, int addressesListSize)
        {
            Throw.IfNull(logger, nameof(logger));

            Thread.CurrentThread.IsBackground = true;

            BigInteger fee = 9999; // TODO calculate the real fee

            BigInteger amount = initialAmount;

            var tcp = new TcpClient("localhost", 7073);
            var peer = new TCPPeer(tcp.Client);

            var peerKey = KeyPair.Generate();
            logger.Message($"#{ID}: Connecting to peer: {host} with address {peerKey.Address.Text}");
            var request = new RequestMessage(RequestKind.None, "simnet", peerKey.Address);
            request.Sign(peerKey);
            peer.Send(request);

            int batchCount = 0;

            var rpc = new JSONRPC_Client();
            {
                logger.Message($"#{ID}: Sending funds to address {peerKey.Address.Text}");
                var hash = SendTransfer(rpc, logger, host, masterKeys, peerKey.Address, initialAmount);
                if (hash == Hash.Null)
                {
                    logger.Error($"#{ID}:Stopping, fund transfer failed");
                    return;
                }

                if (!ConfirmTransaction(rpc, logger, host, hash))
                {
                    logger.Error($"#{ID}:Stopping, fund confirmation failed");
                    return;
                }
            }

            logger.Message($"#{ID}: Beginning send mode");
            bool returnPhase = false;
            var txs = new List<Transaction>();

            var addressList = new List<KeyPair>();
            int waveCount = 0;
            while (true)
            {
                bool shouldConfirm;

                try
                {
                    txs.Clear();

                    
                    if (returnPhase)
                    {
                        foreach (var target in addressList)
                        {
                            var script = ScriptUtils.BeginScript().AllowGas(target.Address, Address.Null, 1, 9999).TransferTokens("SOUL", target.Address, peerKey.Address, 1).SpendGas(target.Address).EndScript();
                            var tx = new Transaction("simnet", "main", script, Timestamp.Now + TimeSpan.FromMinutes(30));
                            tx.Sign(target);
                            txs.Add(tx);
                        }

                        addressList.Clear();
                        returnPhase = false;
                        waveCount = 0;
                        shouldConfirm = true;
                    }
                    else
                    { 
                        amount -= fee * 2 * addressesListSize;
                        if (amount <= 0)
                        {
                            break;
                        }

                        for (int j = 0; j < addressesListSize; j++)
                        {
                            var target = KeyPair.Generate();
                            addressList.Add(target);
                       
                            var script = ScriptUtils.BeginScript().AllowGas(peerKey.Address, Address.Null, 1, 9999).TransferTokens("SOUL", peerKey.Address, target.Address, 1 + fee).SpendGas(peerKey.Address).EndScript();
                            var tx = new Transaction("simnet", "main", script, Timestamp.Now + TimeSpan.FromMinutes(30));
                            tx.Sign(peerKey);
                            txs.Add(tx);
                        }

                        waveCount++;
                        if (waveCount > 10)
                        {
                            returnPhase = true;
                            shouldConfirm = true;
                        }
                        else
                        {
                            shouldConfirm = false;
                        }
                    }

                    returnPhase = !returnPhase;

                    var msg = new MempoolAddMessage(peerKey.Address, txs);
                    msg.Sign(peerKey);
                    peer.Send(msg);
                }
                catch (Exception e)
                {
                    logger.Error($"#{ID}:Fatal error : {e}");
                    return;
                }

                if (txs.Any())
                {
                    if (shouldConfirm)
                    {
                        var confirmation = ConfirmTransaction(rpc, logger, host, txs.Last().Hash);
                        if (!confirmation)
                        {
                            logger.Error($"#{ID}:Confirmation failed, aborting...");
                        }
                    }
                    else
                    {
                        Thread.Sleep(500);
                    }

                    batchCount++;
                    logger.Message($"#{ID}:Sent {txs.Count} transactions (batch #{batchCount})");
                }
                else
                {
                    logger.Message($"#{ID}: No transactions left");
                    return;
                }

            }

            logger.Message($"#{ID}: Thread ran out of funds");
        }

        private void RunSender(string wif, string host, int threadCount, int addressesListSize)
        {
            logger.Message("Running in sender mode.");

            running = true;
            Console.CancelKeyPress += delegate {
                running = false;
                logger.Message("Stopping sender...");
            };

            var masterKeys = KeyPair.FromWIF(wif);

            var rpc = new JSONRPC_Client();
            logger.Message($"Fetch initial balance from {masterKeys.Address}...");
            BigInteger initialAmount = FetchBalance(rpc, logger, host, masterKeys.Address);
            if (initialAmount <= 0)
            {
                logger.Message($"Could not obtain funds");
                return;
            }

            logger.Message($"Initial balance: {UnitConversion.ToDecimal(initialAmount, Nexus.FuelTokenDecimals)} SOUL");

            initialAmount /= 10; // 10%
            initialAmount /= threadCount;

            logger.Message($"Estimated amount per thread: {UnitConversion.ToDecimal(initialAmount, Nexus.FuelTokenDecimals)} SOUL");

            for (int i=1; i<= threadCount; i++)
            {
                logger.Message($"Starting thread #{i}...");
                try
                {
                    new Thread(() => { SenderSpawn(i, masterKeys, host, initialAmount, addressesListSize); }).Start();
                    Thread.Sleep(200);
                }
                catch (Exception e) {
                    logger.Error(e.ToString());
                    break;
                }
            }

            this.Run();
        }

        private void WebLogMapper(string channel, LogLevel level, string text)
        {
            if (gui != null)
            {
                switch (level)
                {
                    case LogLevel.Debug: gui.WriteToChannel(channel, Core.Log.LogEntryKind.Debug, text); break;
                    case LogLevel.Error: gui.WriteToChannel(channel, Core.Log.LogEntryKind.Error, text); break;
                    case LogLevel.Warning: gui.WriteToChannel(channel, Core.Log.LogEntryKind.Warning, text); break;
                    default: gui.WriteToChannel(channel, Core.Log.LogEntryKind.Message, text); break;
                }

                return;
            }

            switch (level)
            {
                case LogLevel.Debug: logger.Debug(text); break;
                case LogLevel.Error: logger.Error(text); break;
                case LogLevel.Warning: logger.Warning(text); break;
                default: logger.Message(text); break;
            }
        }

        public CLI(string[] args) { 
            var seeds = new List<string>();

            var settings = new Arguments(args);

            var useGUI = settings.GetBool("gui.enabled", true);

            if (useGUI)
            {
                gui = new ConsoleGUI();
                logger = gui;
            }
            else
            {
                gui = null;
                logger = new ConsoleLogger();
            }

            string mode = settings.GetString("node.mode", "validator");

            bool hasRPC = settings.GetBool("rpc.enabled", false);
            bool hasREST = settings.GetBool("rest.enabled", false);

            string wif = settings.GetString("node.wif");

            var nexusName = settings.GetString("nexus.name", "simnet");
            var genesisAddress = Address.FromText(settings.GetString("nexus.genesis", KeyPair.FromWIF(validatorWIFs[0]).Address.Text));

            switch (mode)
            {
                case "sender":
                    string host = settings.GetString("sender.host");
                    int threadCount = settings.GetInt("sender.threads", 8);
                    int addressesPerSender = settings.GetInt("sender.addressCount", 100);
                    RunSender(wif, host, threadCount, addressesPerSender);
                    Console.WriteLine("Sender finished operations.");
                    return;

                case "validator": break;
                default:
                    {
                        logger.Error("Unknown mode: " + mode);
                        return;
                    }
            }

            int defaultPort = 0;
            for (int i=0; i<validatorWIFs.Length; i++)
            {
                if (validatorWIFs[i] == wif)
                {
                    defaultPort = (7073 + i);
                }
            }

            if (defaultPort == 0)
            {
                defaultPort = (7073 + validatorWIFs.Length);
            }

            int port = settings.GetInt("node.port", defaultPort);

            var node_keys = KeyPair.FromWIF(wif);

            ChainSimulator simulator;

            if (wif == validatorWIFs[0])
            {
                simulator = new ChainSimulator(node_keys, 1235, logger);
                nexus = simulator.Nexus;
            }
            else
            {
                simulator = null;
                nexus = new Nexus(nexusName, genesisAddress, logger);
                seeds.Add("127.0.0.1:7073");
            }

            // TODO this should be later optional to enable
            nexus.AddPlugin(new ChainAddressesPlugin());
            nexus.AddPlugin(new TokenTransactionsPlugin());
            nexus.AddPlugin(new AddressTransactionsPlugin());
            nexus.AddPlugin(new UnclaimedTransactionsPlugin());

            if (simulator != null)
            {
                for (int i = 0; i < 100; i++)
                {
                    simulator.GenerateRandomBlock();
                }
            }

            running = true;

            // mempool setup
            int blockTime = settings.GetInt("node.blocktime", Mempool.MinimumBlockTime);            
            this.mempool = new Mempool(node_keys, nexus, blockTime);
            mempool.Start(ThreadPriority.AboveNormal);

            mempool.OnTransactionFailed += Mempool_OnTransactionFailed;

            api = new NexusAPI(nexus, mempool);

            // RPC setup
            if (hasRPC)
            {
                int rpcPort = settings.GetInt("rpc.port", 7077);

                logger.Message($"RPC server listening on port {rpcPort}...");
                var rpcServer = new RPCServer(api, "/rpc", rpcPort, (level, text) => WebLogMapper("rpc", level, text));
                rpcServer.Start(ThreadPriority.AboveNormal);
            }

            // REST setup
            if (hasREST)
            {
                int restPort = settings.GetInt("rest.port", 7078);

                logger.Message($"REST server listening on port {restPort}...");
                var restServer = new RESTServer(api, "/api", restPort, (level, text) => WebLogMapper("rest", level, text));
                restServer.Start(ThreadPriority.AboveNormal);
            }

            if (simulator != null && settings.GetBool("simulator", false))
            {
                new Thread(() =>
                {
                    Thread.CurrentThread.IsBackground = true;
                    while (running)
                    {
                        Thread.Sleep(Mempool.MinimumBlockTime + 1000);
                        simulator.CurrentTime = Timestamp.Now;
                        simulator.GenerateRandomBlock(mempool);
                    }
                }).Start();
            }

            // node setup
            this.node = new Node(nexus, mempool, node_keys, port, seeds, logger);           
            node.Start();

            if (gui != null)
            {
                int pluginPeriod = settings.GetInt("plugin.refresh", 1); // in seconds
                RegisterPlugin(new TPSPlugin(logger, pluginPeriod));
                RegisterPlugin(new RAMPlugin(logger, pluginPeriod));
                RegisterPlugin(new MempoolPlugin(mempool, logger, pluginPeriod));
            }

            Console.CancelKeyPress += delegate {
                Terminate();
            };

            logger.Success("Node is ready");

            var dispatcher = new CommandDispatcher();
            SetupCommands(dispatcher);

            if (gui != null)
            {
                gui.MakeReady(dispatcher);
            }

            this.Run();
        }

        private void Mempool_OnTransactionFailed(Transaction tx)
        {
            var status = mempool.GetTransactionStatus(tx.Hash, out string reason);

            logger.Warning($"Rejected transaction {tx.Hash} => "+reason);
        }

        private void Run()
        {
            if (gui != null)
            {
                while (running)
                {
                    gui.Update();
                    this.plugins.ForEach(x => x.Update());
                }
            }
            else
            {
                while (running)
                {
                    Thread.Sleep(1000);
                    this.plugins.ForEach(x => x.Update());
                }
            }
        }

        private void Terminate()
        {
            running = false;
            logger.Message("Phantasma Node stopping...");

            if (node.IsRunning)
            {
                node.Stop();
            }

            if (mempool.IsRunning)
            {
                mempool.Stop();
            }
        }

        private void RegisterPlugin(IPlugin plugin)
        {
            var name = plugin.GetType().Name.Replace("Plugin", "");
            logger.Message("Plugin enabled: " + name);
            plugins.Add(plugin);

            if (nexus != null)
            {
                var nexusPlugin = plugin as IChainPlugin;
                if (nexusPlugin != null)
                {
                    nexus.AddPlugin(nexusPlugin);
                }
            }
        }

        private void ExecuteAPI(string name, string[] args)
        {
            var result = api.Execute(name, args);
            if (result == null)
            {
                logger.Warning("API returned null value...");
                return;
            }

            var node = APIUtils.FromAPIResult(result);
            var json = JSONWriter.WriteToString(node);
            logger.Message(json);
        }

        private void SetupCommands(CommandDispatcher dispatcher)
        {
            dispatcher.RegisterCommand("quit", "Stops the node and exits", (args) => Terminate());

            if (gui != null)
            {
                dispatcher.RegisterCommand("gui.log", "Switches the gui to log view", (args) => gui.ShowLog(args));
                dispatcher.RegisterCommand("gui.graph", "Switches the gui to graph view", (args) => gui.ShowGraph(args));
            }

            dispatcher.RegisterCommand("help", "Lists available commands", (args) => dispatcher.Commands.ToList().ForEach(x => logger.Message($"{x.Name}\t{x.Description}")));

            foreach (var method in api.Methods)
            {
                dispatcher.RegisterCommand("api."+method.Name, "API CALL", (args) => ExecuteAPI(method.Name, args));
            }

            dispatcher.RegisterCommand("script.assemble", "Assembles a .asm file into Phantasma VM script format",
                (args) => ScriptModule.AssembleFile(args));

            dispatcher.RegisterCommand("script.disassemble", $"Disassembles a {ScriptFormat.Extension} file into readable Phantasma assembly",
                (args) => ScriptModule.DisassembleFile(args));

            dispatcher.RegisterCommand("script.compile", "Compiles a .sol file into Phantasma VM script format",
                (args) => ScriptModule.CompileFile(args));
        }

        #region Nacho Server

        private int legendaryLuchadorDelay = 0;
        private int legendaryItemDelay = 0;

        private Dictionary<Rarity, Queue<BigInteger>> itemQueue = new Dictionary<Rarity, Queue<BigInteger>>();
        private Dictionary<Rarity, Queue<Luchador>> luchadorQueue = new Dictionary<Rarity, Queue<Luchador>>();

        private Random rnd = new Random();

        public BigInteger lastItemID;

        public void InitialNachoFill()
        {
            // generate genes for bots
            //GenerateBotGenes();

            //if (contract.debugMode)
            //{
            //    var validCustomMoves = new HashSet<WrestlingMove>(Enum.GetValues(typeof(WrestlingMove)).Cast<WrestlingMove>().Where(x => Rules.CanBeInMoveset(x)));

            //    foreach (var entry in bannedMoves)
            //    {
            //        validCustomMoves.Remove(entry);
            //    }

            //    foreach (var move in validCustomMoves)
            //    {
            //        Console.WriteLine("Generating wrestler with " + move);

            //        var genes = Luchador.MineGenes(rnd, p => WrestlerValidation.IsValidWrestler(p) && p.HasMove(move) && ((p.Rarity == Rarity.Common && move != WrestlingMove.Block) || (move == WrestlingMove.Block && p.Rarity == Rarity.Legendary)));

            //        var rand = new Random();
            //        var isWrapped = rand.Next(0, 100) < 50;

            //        var tx = this.CallContract(owner_keys, "GenerateWrestler", new object[] { owner_keys.Address, genes, (uint)0, isWrapped });
            //        if (tx.content != null)
            //        {
            //            var ID = Serialization.Unserialize<BigInteger>(tx.content);

            //            var luchador = Luchador.FromGenes(0, genes);
            //            Console.WriteLine($"\t\tPrimary Move: {luchador.PrimaryMove}");
            //            Console.WriteLine($"\t\tSecondary Move: {luchador.SecondaryMove}");
            //            Console.WriteLine($"\t\tSupport Move: {luchador.TertiaryMove}");
            //            Console.WriteLine($"\t\tStance Move: {luchador.StanceMove}");

            //            BigInteger price = TokenUtils.ToBigInteger(1);

            //            //var random          = new Random();
            //            //var randomInt       = random.Next(1, 101);
            //            //var randomCurrency  = AuctionCurrency.SOUL;

            //            //if (randomInt > 66)
            //            //{
            //            //    randomCurrency = AuctionCurrency.USD;
            //            //}
            //            //else if (randomInt > 33)
            //            //{
            //            //    randomCurrency = AuctionCurrency.NACHO;
            //            //}

            //            CallContract(owner_keys, "SellWrestler", new object[] { owner_keys.Address, ID, price, price, AuctionCurrency.NACHO, Constants.MAXIMUM_AUCTION_SECONDS_DURATION, "" });
            //        }
            //        else
            //        {
            //            Console.WriteLine("Fatal error: Could not generate wrestler");
            //            Environment.Exit(-1);
            //        }
            //    }

            //    ITEMS

            //   var itemValues = Enum.GetValues(typeof(ItemKind)).Cast<ItemKind>().Where(x => x != ItemKind.None && Rules.IsReleasedItem(x)).ToArray();
            //    var itemExpectedSet = new HashSet<ItemKind>(itemValues);
            //    var itemGeneratedSet = new HashSet<ItemKind>();
            //    var itemIDs = new List<BigInteger>();

            //    while (itemGeneratedSet.Count < itemExpectedSet.Count)
            //    {
            //        var kind = Formulas.GetItemKind(lastItemID);
            //        if (itemExpectedSet.Contains(kind) && !itemGeneratedSet.Contains(kind))
            //        {
            //            Console.WriteLine("Generated item: " + kind);
            //            itemGeneratedSet.Add(kind);
            //            itemIDs.Add(lastItemID);
            //        }
            //        else
            //        if (Rules.IsReleasedItem(kind))
            //        {
            //            EnqueueItem(lastItemID, Rules.GetItemRarity(kind));
            //        }

            //        lastItemID++;
            //    }

            //    foreach (var itemID in itemIDs)
            //    {
            //        var rand = new Random();
            //        var isWrapped = rand.Next(0, 100) < 50;

            //        var tx = this.CallContract(owner_keys, "GenerateItem", new object[] { owner_keys.Address, itemID, isWrapped });
            //        BigInteger price = TokenUtils.ToBigInteger(5);

            //        //var random = new Random();
            //        //var randomInt = random.Next(1, 101);
            //        //var randomCurrency = AuctionCurrency.SOUL;

            //        //if (randomInt > 66)
            //        //{
            //        //    randomCurrency = AuctionCurrency.USD;
            //        //}
            //        //else if (randomInt > 33)
            //        //{
            //        //    randomCurrency = AuctionCurrency.NACHO;
            //        //}

            //        CallContract(owner_keys, "SellItem", new object[] { owner_keys.Address, itemID, price, price, AuctionCurrency.NACHO, Constants.MAXIMUM_AUCTION_SECONDS_DURATION, "" });
            //    }

            //    lastItemID = itemIDs[itemIDs.Count - 1];
            //}

            Console.WriteLine("Filling initial market");
            FillNachoMarket();
        }

        public void FillNachoMarket()
        {
            var luchadorCounts = new Dictionary<Rarity, int>();
            luchadorCounts[Rarity.Common] = 65;
            luchadorCounts[Rarity.Uncommon] = 25;
            luchadorCounts[Rarity.Rare] = 9;
            luchadorCounts[Rarity.Epic] = 1;
            luchadorCounts[Rarity.Legendary] = 1;

            Console.WriteLine("Filling the market with luchadores...");

            foreach (var rarity in luchadorCounts.Keys)
            {
                if (rarity == Rarity.Legendary)
                {
                    if (legendaryLuchadorDelay > 0)
                    {
                        legendaryLuchadorDelay--;
                        continue;
                    }
                    else
                    {
                        legendaryLuchadorDelay = 10;
                    }
                }

                var count = luchadorCounts[rarity];

                for (int i = 1; i <= count; i++)
                {
                    var luchador = DequeueLuchador(rarity);

                    var rand = new Random();
                    var isWrapped = rand.Next(0, 100) < 50;

                    /* TODO fix
                    var tx = this.CallContract(owner_keys, "GenerateWrestler", new object[] { owner_keys.Address, luchador.data.genes, 0, isWrapped });
                    var ID = Serialization.Unserialize<BigInteger>(tx.content);
                    */

                    decimal minPrice, maxPrice;

                    GetLuchadorPriceRange(rarity, out minPrice, out maxPrice);
                    var diff = (int)(maxPrice - minPrice);
                    var price = (decimal)(minPrice + rnd.Next() % diff);

                    if (price < 0) price *= -1; // HACK

                    //var random = new Random();
                    //var randomInt = random.Next(1, 101);
                    //var randomCurrency = AuctionCurrency.SOUL;

                    //if (randomInt > 66)
                    //{
                    //    randomCurrency = AuctionCurrency.USD;
                    //}
                    //else if (randomInt > 33)
                    //{
                    //    randomCurrency = AuctionCurrency.NACHO;
                    //}

                    /* TODO fix
                    CallContract(owner_keys, "SellWrestler", new object[] { owner_keys.Address, ID, TokenUtils.ToBigInteger(price), TokenUtils.ToBigInteger(price), AuctionCurrency.NACHO, NachoConstants.MAXIMUM_AUCTION_SECONDS_DURATION, "" });
                    */

                }
            }

            // TODO que valores s�o estes? se isto est� hardcoded ent�o est� desactualizado pq j� existem mais items
            var itemCounts = new Dictionary<Rarity, int>();
            itemCounts[Rarity.Common] = 16;
            itemCounts[Rarity.Uncommon] = 12;
            itemCounts[Rarity.Rare] = 8;
            itemCounts[Rarity.Epic] = 2;
            itemCounts[Rarity.Legendary] = 1;

            Console.WriteLine("Generating items for market...");

            foreach (var rarity in itemCounts.Keys)
            {
                if (rarity == Rarity.Legendary)
                {
                    if (legendaryItemDelay > 0)
                    {
                        legendaryItemDelay--;
                        continue;
                    }
                    else
                    {
                        legendaryItemDelay = 10;
                    }
                }

                var count = itemCounts[rarity];

                for (int i = 1; i <= count; i++)
                {
                    var itemID = DequeueItem(rarity);

                    var rand = new Random();
                    var isWrapped = rand.Next(0, 100) < 50;

                    /* TODO fix
                    var tx = this.CallContract(owner_keys, "GenerateItem", new object[] { owner_keys.Address, itemID, isWrapped });
                    */
                    decimal minPrice, maxPrice;

                    GetItemPriceRange(rarity, out minPrice, out maxPrice);
                    var diff = (int)(maxPrice - minPrice);
                    var price = (decimal)(minPrice + rnd.Next() % diff);

                    if (price < 0) price *= -1; // HACK

                    //var random = new Random();
                    //var randomInt = random.Next(1, 101);
                    //var randomCurrency = AuctionCurrency.SOUL;

                    //if (randomInt > 66)
                    //{
                    //    randomCurrency = AuctionCurrency.USD;
                    //}
                    //else if (randomInt > 33)
                    //{
                    //    randomCurrency = AuctionCurrency.NACHO;
                    //}

                    /* TODO FIX
                    CallContract(owner_keys, "SellItem", new object[] { owner_keys.Address, itemID, TokenUtils.ToBigInteger(price), TokenUtils.ToBigInteger(price), AuctionCurrency.NACHO, NachoConstants.MAXIMUM_AUCTION_SECONDS_DURATION, "" });
                    */
                }
            }
        }

        public static void GenerateBotGenes()
        {
            var rnd = new System.Random();

            for (int n = 1; n <= 8; n++)
            {
                var level = (PraticeLevel)n;

                /*HashSet<WrestlingMove> wantedMoves;

                switch (level)
                {
                    case PraticeLevel.Wood: wantedMoves = new HashSet<WrestlingMove>(new WrestlingMove[] { WrestlingMove.Bash }); break;
                    case PraticeLevel.Iron: wantedMoves = new HashSet<WrestlingMove>(new WrestlingMove[] { WrestlingMove.Block }); break;
                    case PraticeLevel.Steel: wantedMoves = new HashSet<WrestlingMove>(new WrestlingMove[] { WrestlingMove.Corkscrew }); break;
                    case PraticeLevel.Silver: wantedMoves = new HashSet<WrestlingMove>(new WrestlingMove[] { WrestlingMove.Bulk }); break;
                    case PraticeLevel.Gold: wantedMoves = new HashSet<WrestlingMove>(new WrestlingMove[] { WrestlingMove.Chicken_Wing}); break;
                    case PraticeLevel.Ruby: wantedMoves = new HashSet<WrestlingMove>(new WrestlingMove[] { WrestlingMove.Refresh }); break;
                    case PraticeLevel.Emerald: wantedMoves = new HashSet<WrestlingMove>(new WrestlingMove[] { WrestlingMove.Rhino_Charge }); break;
                    case PraticeLevel.Diamond: wantedMoves = new HashSet<WrestlingMove>(new WrestlingMove[] { WrestlingMove.Razor_Jab }); break;
                    default: wantedMoves = new HashSet<WrestlingMove>(validMoves); break;
                }*/

                //level = PraticeLevel.Wood;
                Console.WriteLine("Mining bot: " + level);

                //var genes = Luchador.MineBotGenes(rnd, level/*, wantedMoves*/);

                //for (int i = 0; i < genes.Length; i++)
                //{
                //    Console.Write(genes[i] + ", ");
                //}

                //var bb = Luchador.FromGenes(n, genes);
                //var temp = bb.data;
                //bb.data = temp;

                //Console.WriteLine();
                //Console.WriteLine(bb.Name);
                ////Console.WriteLine("Kind: " + bb.Rarity);
                ////Console.WriteLine("Head: " + bb.GetBodyPart(BodyPart.Head).Variation);
                //Console.WriteLine("Primary Move: " + bb.PrimaryMove);
                //Console.WriteLine("Secondary Move: " + bb.SecondaryMove);
                //Console.WriteLine("Support Move: " + bb.TertiaryMove);
                //Console.WriteLine("Stance Move: " + bb.StanceMove);
                //Console.WriteLine("Base STA: " + bb.BaseStamina);
                //Console.WriteLine("Base ATK: " + bb.BaseAttack);
                //Console.WriteLine("Base DEF: " + bb.BaseDefense);
                Console.WriteLine("------------------");
            }
        }

        public static void GetLuchadorPriceRange(Rarity level, out decimal min, out decimal max)
        {
            switch (level)
            {
                case Rarity.Common: min = 7; max = 10; break;
                case Rarity.Uncommon: min = 75m; max = 100; break;
                case Rarity.Rare: min = 750m; max = 1000; break;
                case Rarity.Epic: min = 6500m; max = 7000; break;
                case Rarity.Legendary: min = 30000m; max = 35000; break;
                default: min = 7.5m; max = 10; break;
            }
        }

        public static void GetItemPriceRange(Rarity level, out decimal min, out decimal max)
        {
            switch (level)
            {
                case Rarity.Common: min = 3; max = 5; break;
                case Rarity.Uncommon: min = 35m; max = 50; break;
                case Rarity.Rare: min = 350m; max = 500; break;
                case Rarity.Epic: min = 3000m; max = 3500; break;
                case Rarity.Legendary: min = 15000m; max = 20000; break;
                default: min = 3.5m; max = 30; break;
            }
        }

        private void MineRandomItems(int amount)
        {
            while (amount > 0)
            {
                lastItemID++;
                var obtained = Formulas.GetItemKind(lastItemID);

                if (Rules.IsReleasedItem(obtained))
                {
                    var rarity = Rules.GetItemRarity(obtained);
                    EnqueueItem(lastItemID, rarity);
                    amount--;
                }

            }
        }

        private void EnqueueItem(BigInteger ID, Rarity rarity)
        {
            Queue<BigInteger> queue;

            if (itemQueue.ContainsKey(rarity))
            {
                queue = itemQueue[rarity];
            }
            else
            {
                queue = new Queue<BigInteger>();
                itemQueue[rarity] = queue;
            }

            queue.Enqueue(ID);
        }

        private BigInteger DequeueItem(Rarity rarity)
        {
            while (!itemQueue.ContainsKey(rarity) || itemQueue[rarity].Count == 0)
            {
                MineRandomItems(10);
            }

            return itemQueue[rarity].Dequeue();
        }

        private void MineRandomLuchadores(int amount)
        {
            while (amount > 0)
            {
                var genes = Luchador.MineGenes(rnd, null);

                var luchador = Luchador.FromGenes(0, genes);

                if (WrestlerValidation.IsValidWrestler(luchador))
                {
                    amount--;
                    EnqueueLuchador(luchador);
                }
            }
        }

        private HashSet<WrestlingMove> bannedMoves = new HashSet<WrestlingMove>(new WrestlingMove[]
        {
            WrestlingMove.Pray,
            WrestlingMove.Hyper_Slam,
        });

        private void EnqueueLuchador(Luchador luchador)
        {
            Queue<Luchador> queue;

            var rarity = luchador.Rarity;
            if (luchadorQueue.ContainsKey(rarity))
            {
                queue = luchadorQueue[rarity];
            }
            else
            {
                queue = new Queue<Luchador>();
                luchadorQueue[rarity] = queue;
            }

            queue.Enqueue(luchador);
        }

        private Luchador DequeueLuchador(Rarity rarity)
        {
            while (!luchadorQueue.ContainsKey(rarity) || luchadorQueue[rarity].Count == 0)
            {
                MineRandomLuchadores(10);
            }

            return luchadorQueue[rarity].Dequeue();
        }

        #endregion
    }
}
