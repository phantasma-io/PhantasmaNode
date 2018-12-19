﻿using System;
using System.Collections.Generic;
using System.Threading;
using Phantasma.Blockchain.Consensus;
using Phantasma.Blockchain;
using Phantasma.Cryptography;
using Phantasma.Core.Log;
using Phantasma.Tests;
using Phantasma.Core.Utils;
using Phantasma.Numerics;
using Phantasma.Core.Types;
using Phantasma.Blockchain.Tokens;
using LunarLabs.Parser.JSON;
using LunarLabs.Parser;

namespace Phantasma.CLI
{
    class Program
    {
        private static readonly string[] validatorWIFs = new string[]
        {
            "L2LGgkZAdupN2ee8Rs6hpkc65zaGcLbxhbSDGq8oh6umUxxzeW25", //P2f7ZFuj6NfZ76ymNMnG3xRBT5hAMicDrQRHE4S7SoxEr
            "L1sEB8Z6h5Y7aQKqxbAkrzQLY5DodmPacjqSkFPmcR82qdmHEEdY", // PGBinkbZA3Q6BxMnL2HnJSBubNvur3iC6GtQpEThDnvrr
            "KxWUCAD2wECLfA7diT7sV7V3jcxAf9GSKqZy3cvAt79gQLHQ2Qo8", // PDiqQHDwe6MTcP6TH6DYjq7FTUouvy2YEkDXz2chCABCb
        };

        static void PrintChain(Chain chain)
        {
            Console.WriteLine("Listing blocks...");
            foreach (var block in chain.Blocks)
            {
                Console.WriteLine("Block #" + block.Height);
                Console.WriteLine("\tHash: " + block.Hash);

                Console.WriteLine("\tTransactions: ");
                int index = 0;
                foreach (var hash in block.TransactionHashes)
                {
                    var tx = chain.FindTransactionByHash(hash);
                    Console.WriteLine("\t\tTransaction #" + index);
                    Console.WriteLine("\t\tHash: " + tx.Hash);
                    Console.WriteLine();

                    index++;
                }

                Console.WriteLine("\tEvents: ");
                index = 0;
                foreach (var hash in block.TransactionHashes)
                {
                    var events = block.GetEventsForTransaction(hash);

                    foreach (var evt in events)
                    {
                        Console.WriteLine("\t\tEvent #" + index);
                        Console.WriteLine("\t\tKind: " + evt.Kind);
                        Console.WriteLine("\t\tTarget: " + evt.Address.Text);
                        Console.WriteLine();

                        index++;
                    }
                }

                Console.WriteLine();
            }
        }

        private static void SendTransfer(string host, KeyPair from, Address to, BigInteger amount)
        {
            var script = ScriptUtils.BeginScript().AllowGas(from.Address, 1, 9999).TransferTokens("SOUL", from.Address, to, amount).SpendGas(from.Address).EndScript();

            var tx = new Transaction("simnet", "main", script, Timestamp.Now + TimeSpan.FromMinutes(30), 0);
            tx.Sign(from);

            var bytes = tx.ToByteArray(true);

            var paramData = DataNode.CreateArray("params");
            paramData.AddField(null, Base16.Encode(bytes));

            var jsonRpcData = DataNode.CreateObject(null);
            jsonRpcData.AddField("jsonrpc", "2.0");
            jsonRpcData.AddField("method", "sendRawTransaction");
            jsonRpcData.AddNode(paramData);
            jsonRpcData.AddField("id", "1");

            var response = JSONRequest.Execute(RequestType.POST, host, jsonRpcData);
        }

        static void RunSender(string wif, string host)
        {
            Console.WriteLine("Running in sender mode.");
            var initialKey = KeyPair.Generate();

            SendTransfer(host, KeyPair.FromWIF(wif), initialKey.Address, TokenUtils.ToBigInteger(1000, Nexus.NativeTokenDecimals));
        }

        static void Main(string[] args)
        {
            var log = new ConsoleLogger();
            var seeds = new List<string>();

            Console.ForegroundColor = ConsoleColor.DarkGray;

            var settings = new Arguments(args);

            string mode = settings.GetValue("mode", "validator");

            string wif = settings.GetValue("wif");
            var nexusName = settings.GetValue("nexus", "simnet");
            var genesisAddress = Address.FromText(settings.GetValue("genesis", KeyPair.FromWIF(validatorWIFs[0]).Address.Text));

            switch (mode)
            {
                case "sender":
                    string host = settings.GetValue("host");
                    RunSender(host, wif);
                    return;

                case "validator": break;
                default:
                    {
                        log.Error("Unknown mode: " + mode);
                        return;
                    }
            }

            string defaultPort = null;
            for (int i=0; i<validatorWIFs.Length; i++)
            {
                if (validatorWIFs[i] == wif)
                {
                    defaultPort = (7073 + i).ToString();
                }
            }

            if (defaultPort == null)
            {
                defaultPort = (7073 + validatorWIFs.Length).ToString();
            }

            int port = int.Parse(settings.GetValue("port", defaultPort));

            var node_keys = KeyPair.FromWIF(wif);

            Nexus nexus;

            if (wif == validatorWIFs[0])
            {
                var simulator = new ChainSimulator(node_keys, 1234);
                nexus = simulator.Nexus;

                for (int i=0; i< 1000; i++)
                {
                    simulator.GenerateRandomBlock();
                }
            }
            else
            {
                nexus = new Nexus(nexusName, genesisAddress, log);
                seeds.Add("127.0.0.1:7073");
            }

            var node = new Node(nexus, node_keys, port, seeds, log);

            bool running = true;

            log.Message("Phantasma Node address: " + node_keys.Address.Text);
            node.Start();

            Console.CancelKeyPress += delegate {
                running = false;
                log.Message("Phantasma Node stopping...");
                node.Stop();
            };

            while (running)
            {
                Thread.Sleep(1000);
            }
        }
    }
}
