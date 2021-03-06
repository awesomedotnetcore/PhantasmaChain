﻿using Phantasma.Blockchain.Storage;
using Phantasma.Core.Types;
using Phantasma.Cryptography;
using Phantasma.Numerics;

namespace Phantasma.Blockchain.Contracts.Native
{
    public struct EnergyAction
    {
        public BigInteger amount;
        public Timestamp timestamp;
    }

    public struct EnergyProxy
    {
        public Address address;
        public byte percentage;
    }

    public sealed class EnergyContract : SmartContract
    {
        public override string Name => "energy";

        private StorageMap _stakes; // <Address, EnergyAction>
        private StorageMap _proxyMap; // <Address, List<EnergyProxy>>
        private StorageMap _claims; // <Address, EnergyAction>

        public readonly static BigInteger EnergyRacioDivisor = 500; // used as 1/500, will generate 0.002 per staked token
 
        public EnergyContract() : base()
        {
        }

        public void Stake(Address from, BigInteger stakeAmount)
        {
            Runtime.Expect(stakeAmount >= EnergyRacioDivisor, "invalid amount");
            Runtime.Expect(IsWitness(from), "witness failed");

            var stakeToken = Runtime.Nexus.StakingToken;
            var stakeBalances = Runtime.Chain.GetTokenBalances(stakeToken);
            var balance = stakeBalances.Get(from);
            Runtime.Expect(balance >= stakeAmount, "not enough balance");

            Runtime.Expect(stakeBalances.Subtract(from, stakeAmount), "balance subtract failed");
            Runtime.Expect(stakeBalances.Add(Runtime.Chain.Address, stakeAmount), "balance add failed");


            var entry = new EnergyAction()
            {
                amount = stakeAmount,
                timestamp = Timestamp.Now,
            };
            _stakes.Set(from, entry);

            Runtime.Notify(EventKind.TokenStake, from, new TokenEventData() { chainAddress = Runtime.Chain.Address, symbol = stakeToken.Symbol, value = stakeAmount });
        }

        public BigInteger Unstake(Address from)
        {
            Runtime.Expect(IsWitness(from), "witness failed");

            var entry = _stakes.Get<Address, EnergyAction>(from);

            var diff = Timestamp.Now - entry.timestamp;
            var days = diff / 86400; // convert seconds to days

            Runtime.Expect(days >= 1, "waiting period required");

            var amount = entry.stake;
            var token = Runtime.Nexus.StakingToken;
            var balances = Runtime.Chain.GetTokenBalances(token);
            var balance = balances.Get(Runtime.Chain.Address);
            Runtime.Expect(balance >= amount, "not enough balance");

            Runtime.Expect(balances.Subtract(Runtime.Chain.Address, amount), "balance subtract failed");
            Runtime.Expect(balances.Add(from, amount), "balance add failed");

            _stakes.Remove(from);

            Runtime.Notify(EventKind.TokenUnstake, from, new TokenEventData() { chainAddress = Runtime.Chain.Address, symbol = token.Symbol, value = amount });

            return amount;
        }

        public void Claim(Address from, Address stakeAddress)
        {
            Runtime.Expect(IsWitness(from), "witness failed");

            var stake = _stakes.Get<Address, EnergyAction>(stakeAddress);
            var unclaimedAmount = stake.amount;

            var lastClaim = _claims.Get<Address, EnergyAction>(stakeAddress);
            var diff = Timestamp.Now - lastClaim.timestamp;

            var days = diff / 86400; // convert seconds to days

            // if not enough time has passed, deduct the last claim from the available amount
            if (days < 0)
            {
                unclaimedAmount -= lastClaim.amount;
            }

            Runtime.Expect(unclaimedAmount > 0, "nothing unclaimed");

            var stakeToken = Runtime.Nexus.StakingToken;

            var fuelToken = Runtime.Nexus.FuelToken;
            var fuelBalances = Runtime.Chain.GetTokenBalances(stakeToken);
            var fuelAmount = unclaimedAmount / EnergyRacioDivisor;

            // distribute to proxy list
            var list = _proxyMap.Get<Address, StorageList>(stakeAddress);
            var count = list.Count();

            // if the transaction comes from someone other than the stake owner, must be registred in proxy list
            if (from != stakeAddress)
            {
                bool found = false;
                for (int i = 0; i < count; i++)
                {
                    var proxy = list.Get<EnergyProxy>(i);
                    if (proxy.address == from)
                    {
                        found = true;
                        break;
                    }
                }
                Runtime.Expect(found, "invalid permissions");
            }

            BigInteger sum = 0;
            BigInteger availableAmount = fuelAmount;
            for (int i = 0; i < count; i++)
            {
                var proxy = list.Get<EnergyProxy>(i);
                sum += proxy.percentage;

                var proxyAmount = (fuelAmount * proxy.percentage) / 100;
                if (proxyAmount > 0)
                {
                    Runtime.Expect(availableAmount >= proxyAmount, "unsuficient amount for proxy distribution");
                    Runtime.Expect(fuelToken.Mint(fuelBalances, proxy.address, proxyAmount), "proxy fuel minting failed");
                    availableAmount -= proxyAmount;
                }
            }

            Runtime.Expect(availableAmount >= 0, "unsuficient leftovers");
            Runtime.Expect(fuelToken.Mint(fuelBalances, stakeAddress, availableAmount), "fuel minting failed");

            // NOTE here we set the full staked amount instead of claimed amount, to avoid infinite claims loophole
            var action = new EnergyAction() { amount = stake.amount, timestamp = Timestamp.Now };
            _claims.Set<Address, EnergyAction>(stakeAddress, action);

            Runtime.Notify(EventKind.TokenClaim, from, new TokenEventData() { chainAddress = Runtime.Chain.Address, symbol = stakeToken.Symbol, value = unclaimedAmount});
            Runtime.Notify(EventKind.TokenMint, stakeAddress, new TokenEventData() { chainAddress = Runtime.Chain.Address, symbol = fuelToken.Symbol, value = fuelAmount });
        }

        public BigInteger GetStake(Address address)
        {
            Runtime.Expect(_stakes.ContainsKey(address), "not a validator address");
            var entry = _stakes.Get<Address, EnergyAction>(address);
            return entry.stake;
        }

        public EnergyProxy[] GetProxies(Address address)
        {
            var list = _proxyMap.Get<Address, StorageList>(address);
            return list.All<EnergyProxy>();
        }

        public void ClearProxies(Address from)
        {
            Runtime.Expect(IsWitness(from), "invalid witness");

            var list = _proxyMap.Get<Address, StorageList>(from);
            var count = list.Count();
            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var proxy = list.Get<EnergyProxy>(i);
                    Runtime.Notify(EventKind.AddressRemove, from, proxy.address);

                }
                list.Clear();
            }
        }

        public void AddProxy(Address from, Address to, BigInteger percentage)
        {
            Runtime.Expect(percentage > 0, "invalid percentage");
            Runtime.Expect(percentage <= 100, "invalid percentage");
            Runtime.Expect(from != to, "invalid proxy address");
            Runtime.Expect(IsWitness(from), "invalid witness");

            var list = _proxyMap.Get<Address, StorageList>(from);

            BigInteger sum = 0;
            int index = -1;
            var count = list.Count();
            for (int i = 0; i < count; i++)
            {
                var proxy = list.Get<EnergyProxy>(i);
                if (proxy.address == to)
                {
                    sum += percentage;
                    index = i;
                }
                else
                {
                    sum += proxy.percentage;
                }
            }

            Runtime.Expect(sum <= 100, "invalid sum");

            var entry = new EnergyProxy() { percentage = (byte)percentage, address = to };
            if (index < 0)
            {
                list.Add<EnergyProxy>(entry);
            }
            else
            {
                list.Replace<EnergyProxy>(index, entry);
            }

            Runtime.Notify(EventKind.AddressAdd, from, to);
        }

        public void RemoveProxy(Address from, Address to)
        {
            Runtime.Expect(from != to, "invalid proxy address");
            Runtime.Expect(IsWitness(from), "invalid witness");

            var list = _proxyMap.Get<Address, StorageList>(from);

            int index = -1;
            var count = list.Count();
            for (int i = 0; i < count; i++)
            {
                var proxy = list.Get<EnergyProxy>(i);
                if (proxy.address == to)
                {
                    index = i;
                    break;
                }
            }
           
            Runtime.Expect(index>=0, "proxy not found");

            list.RemoveAt<EnergyProxy>(index);
            Runtime.Notify(EventKind.AddressRemove, from, to);
        }

    }
}
