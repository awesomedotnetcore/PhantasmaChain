﻿using Phantasma.Cryptography;
using Phantasma.Cryptography.ECC;
using System;
using System.Linq;

namespace Phantasma.Pay.Chains
{
    public class NeoWallet : CryptoWallet
    {
        public NeoWallet(KeyPair keys) : base(keys)
        {
        }

        public override WalletKind Kind => WalletKind.Neo;

        public override void MakePayment(string symbol, decimal amount, string targetAddress, Action<bool> callback)
        {
            throw new NotImplementedException();
        }

        public override void SyncBalances(Action<bool> callback)
        {
            _balances.Clear();

            var url = "https://api.neoscan.io/api/main_net/v1/get_balance/" + this.Address;
            JSONRequest(url, (root) =>
            {
                if (root == null)
                {
                    callback(false);
                    return;
                }

                root = root.GetNode("balance");
                foreach (var child in root.Children)
                {
                    var symbol = child.GetString("asset_symbol");
                    var amount = child.GetDecimal("amount");
                    _balances.Add(new WalletBalance(symbol, amount));
                }

                callback(true);
            });
        }

        protected override string DeriveAddress(KeyPair keys)
        {
            ECPoint pKey = ECCurve.Secp256r1.G * keys.PrivateKey;

            var bytes = pKey.EncodePoint(true);

            var script = new byte[bytes.Length + 2];
            script[0] = 0x21;// OpCode.PUSHBYTES33;
            Array.Copy(bytes, 0, script, 1, bytes.Length);
            script[script.Length - 1] = 0xAC; // OpCode.CHECKSIG;

            var scriptHash = script.SHA256().RIPEMD160();

            //this.PublicKey = pKey.EncodePoint(false).Skip(1).ToArray();

            byte[] data = new byte[21];
            data[0] = 23;
            Buffer.BlockCopy(scriptHash.ToArray(), 0, data, 1, 20);
            return data.Base58CheckEncode();
        }
    }
}
