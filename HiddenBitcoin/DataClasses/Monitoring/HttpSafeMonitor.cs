﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HiddenBitcoin.DataClasses.Balances;
using HiddenBitcoin.DataClasses.Histories;
using HiddenBitcoin.DataClasses.KeyManagement;
using NBitcoin;
using QBitNinja.Client;
using QBitNinja.Client.Models;

namespace HiddenBitcoin.DataClasses.Monitoring
{
    public class HttpSafeMonitor : HttpMonitor, INotifyPropertyChanged
    {
        private readonly WalletClient _qBitNinjaWalletClient;

        private int _initializationProgressPercent;
        private State _initializationState;
        private SafeBalanceInfo _safeBalanceInfo;
        private SafeHistory _safeHistory;
        private decimal _unconfirmedBalance;

        public HttpSafeMonitor(Safe safe, int addressCount) : base(safe.Network)
        {
            AssertNetwork(safe.Network);
            AddressCount = addressCount;
            Safe = new HttpSafe(safe, addressCount);

            _qBitNinjaWalletClient = Client.GetWalletClient(QBitNinjaWalletName);
            _qBitNinjaWalletClient.CreateIfNotExists().Wait();

            StartInitializingQBitNinjaWallet();
        }

        public SafeHistory SafeHistory
        {
            get
            {
                if (_safeHistory == null)
                    UpdateSafeHistoryAndBalanceInfo();
                return _safeHistory;
            }
            private set { _safeHistory = value; }
        }

        public State InitializationState
        {
            get { return _initializationState; }
            private set
            {
                if (value == _initializationState) return;
                _initializationState = value;
                OnPropertyChanged();
                OnInitializationStateChanged();
            }
        }

        public int InitializationProgressPercent
        {
            get { return _initializationProgressPercent; }
            private set
            {
                if (value == _initializationProgressPercent) return;

                switch (value)
                {
                    case 0:
                        InitializationState = State.NotStarted;
                        break;
                    case 100:
                        InitializationState = State.Ready;
                        break;
                    default:
                        if (value > 0 && value < 100) InitializationState = State.InProgress;
                        else
                            throw new ArgumentOutOfRangeException(
                                $"InitializationProgressPercent cannot be {value}. It must be >=0 and <=100");
                        break;
                }
                _initializationProgressPercent = value;
                OnPropertyChanged();
                OnInitializationProgressPercentChanged();
            }
        }

        public int AddressCount { get; }

        public HttpSafe Safe { get; }

        private string QBitNinjaWalletName
        {
            get
            {
                // Let's generate the walletname from seedpublickey
                var bitcoinExtPubKey = new BitcoinExtPubKey(Safe.SeedPublicKey);
                // Let's get the pubkey, so the chaincode is lost
                var pubKey = bitcoinExtPubKey.ExtPubKey.PubKey;
                // Let's get the address, you can't directly access it from the safe
                // Also nobody would ever use this address for anything
                var address = pubKey.GetAddress(_Network).ToWif();
                // Let's just simply add the addresscount so in case we have the same safe, but different
                // sizes it should be in an other wallet
                return address + AddressCount;
            }
        }

        public List<string> MonitoredAddresses
        {
            get
            {
                var monitoredAddresses = new List<string>();
                for (var i = 0; i < AddressCount; i++)
                {
                    monitoredAddresses.Add(Safe.GetAddress(i));
                }
                return monitoredAddresses;
            }
        }

        private decimal UnconfirmedBalance
        {
            set
            {
                if (value == _unconfirmedBalance)
                    return;
                _unconfirmedBalance = value;
                OnBalanceChanged();
            }
        }

        public SafeBalanceInfo SafeBalanceInfo
        {
            get
            {
                if (_safeBalanceInfo == null)
                    UpdateSafeHistoryAndBalanceInfo();
                return _safeBalanceInfo;
            }
            set
            {
                _safeBalanceInfo = value;
                UnconfirmedBalance = value.Unconfirmed;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        // ReSharper disable once FunctionNeverReturns https://youtrack.jetbrains.com/issue/RSRP-425337
        private async void PeriodicUpdate()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                if (InitializationState == State.Ready)
                    UpdateSafeHistoryAndBalanceInfo();
            }
        }

        protected void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            PropertyChanged?.Invoke(this, e);
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public event EventHandler InitializationStateChanged;
        public event EventHandler InitializationProgressPercentChanged;
        public event EventHandler BalanceChanged;

        protected virtual void OnInitializationStateChanged()
        {
            InitializationStateChanged?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void OnInitializationProgressPercentChanged()
        {
            InitializationProgressPercentChanged?.Invoke(this, EventArgs.Empty);
        }

        public async void StartInitializingQBitNinjaWallet()
        {
            await Task.Run(() =>
            {
                InitializationState = State.NotStarted;
                InitializationProgressPercent = 0;
                List<string> outOfSyncAddresses;
                do
                {
                    outOfSyncAddresses = GetOutOfSyncAddresses();
                    AdjustState(AddressCount - outOfSyncAddresses.Count);

                    if (outOfSyncAddresses.Count == 0) continue;

                    foreach (var address in outOfSyncAddresses)
                    {
                        AdjustState(outOfSyncAddresses.IndexOf(address));
                        _qBitNinjaWalletClient.CreateAddressIfNotExists(new BitcoinPubKeyAddress(address));
                    }
                } while (outOfSyncAddresses.Count != 0);
            });

            PeriodicUpdate();
        }

        private void AdjustState(int syncedAddressCount)
        {
            if (syncedAddressCount < 0 || syncedAddressCount > AddressCount)
                throw new ArgumentOutOfRangeException(
                    $"syncedAddressCount cannot be {syncedAddressCount}. It must be >=0 and <=AddressCount");

            InitializationProgressPercent = (int) Math.Round((double) (100*syncedAddressCount)/AddressCount);
        }

        private List<string> GetOutOfSyncAddresses()
        {
            var qbitAddresses = _qBitNinjaWalletClient.GetAddresses().Result.Select(x => x.Address.ToWif()).ToList();
            var safeAddresses = new List<string>();
            for (var i = 0; i < AddressCount; i++)
            {
                safeAddresses.Add(Safe.GetAddress(i));
            }

            if (qbitAddresses.Any(qbitAddress => !safeAddresses.Contains(qbitAddress)))
            {
                throw new Exception("QBitNinja wallet and HTTPSafeMonitor is out of sync.");
            }

            return safeAddresses.Where(safeAddress => !qbitAddresses.Contains(safeAddress)).ToList();
        }

        public SafeBalanceInfo GetSafeBalanceInfo()
        {
            AssertState();

            UpdateSafeHistoryAndBalanceInfo();

            return SafeBalanceInfo;
        }

        private void UpdateSafeHistoryAndBalanceInfo()
        {
            AssertState();

            var balanceOperations = _qBitNinjaWalletClient.GetBalance().Result.Operations;

            // Find all the operations concerned to one address
            // address, balanceoperationlist
            var addressOperationPairs = new List<Tuple<string, BalanceOperation>>();
            // address, unconfirmed, confirmed
            var receivedAddressAmountPairs = new List<Tuple<string, decimal, decimal>>();
            var spentAddressAmountPairs = new List<Tuple<string, decimal, decimal>>();
            foreach (var operation in balanceOperations)
            {
                foreach (var coin in operation.ReceivedCoins)
                {
                    string address;
                    if (!SafeContainsCoin(out address, coin)) continue;

                    var amount = operation.Amount.ToDecimal(MoneyUnit.BTC);
                    receivedAddressAmountPairs.Add(operation.Confirmations == 0
                        ? new Tuple<string, decimal, decimal>(address, amount, 0m)
                        : new Tuple<string, decimal, decimal>(address, 0m, amount));

                    addressOperationPairs.Add(new Tuple<string, BalanceOperation>(address, operation));
                }

                foreach (var coin in operation.SpentCoins)
                {
                    string address;
                    if (!SafeContainsCoin(out address, coin)) continue;

                    var amount = operation.Amount.ToDecimal(MoneyUnit.BTC);
                    spentAddressAmountPairs.Add(operation.Confirmations == 0
                        ? new Tuple<string, decimal, decimal>(address, amount, 0m)
                        : new Tuple<string, decimal, decimal>(address, 0m, amount));

                    addressOperationPairs.Add(new Tuple<string, BalanceOperation>(address, operation));
                }
            }

            var addressOperationsDict = new Dictionary<string, List<BalanceOperation>>();
            foreach (var pair in addressOperationPairs)
                if (addressOperationsDict.Keys.Contains(pair.Item1))
                    addressOperationsDict[pair.Item1].Add(pair.Item2);
                else
                    addressOperationsDict.Add(pair.Item1, new List<BalanceOperation> {pair.Item2});

            var addressHistories =
                addressOperationsDict.Select(pair => new AddressHistory(pair.Key, pair.Value)).ToList();


            SafeHistory = new SafeHistory(Safe, addressHistories);

            var uniqueAddresses = new HashSet<string>();
            foreach (var pair in receivedAddressAmountPairs)
                uniqueAddresses.Add(pair.Item1);
            foreach (var pair in spentAddressAmountPairs)
                uniqueAddresses.Add(pair.Item1);

            var addressBalanceInfos = new List<AddressBalanceInfo>();

            foreach (var address in uniqueAddresses)
            {
                var unconfirmed = 0m;
                var confirmed = 0m;
                foreach (var pair in spentAddressAmountPairs)
                {
                    if (pair.Item1 != address) continue;
                    unconfirmed -= pair.Item2;
                    confirmed -= pair.Item3;
                }
                foreach (var pair in receivedAddressAmountPairs)
                {
                    if (pair.Item1 != address) continue;
                    unconfirmed += pair.Item2;
                    confirmed += pair.Item3;
                }

                addressBalanceInfos.Add(new AddressBalanceInfo(address, unconfirmed, confirmed));
            }

            addressBalanceInfos.AddRange(from address in MonitoredAddresses
                where !uniqueAddresses.Contains(address)
                select new AddressBalanceInfo(address, 0m, 0m));

            SafeBalanceInfo = new SafeBalanceInfo(Safe, addressBalanceInfos);
        }

        public SafeHistory GetSafeHistory()
        {
            AssertState();

            UpdateSafeHistoryAndBalanceInfo();

            return SafeHistory;
        }

        private bool SafeContainsCoin(out string address, ICoin coin)
        {
            try
            {
                address = coin.GetScriptCode().GetDestinationAddress(_Network).ToWif();
            }
            catch
            {
                // Not concerned, safe can't contain something like this
                address = null;
                return false;
            }
            return MonitoredAddresses.Contains(address);
        }

        private void AssertState()
        {
            if (InitializationState != State.Ready)
                throw new Exception("HttpSafeMonitor is not synced with QBitNinja wallet.");
        }

        protected virtual void OnBalanceChanged()
        {
            BalanceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public class HttpSafe : Safe
    {
        public HttpSafe(Safe safe, int addressCount) : base(safe)
        {
            AddressCount = addressCount;
        }

        public int AddressCount { get; set; }

        public override string GetAddress(int index)
        {
            AssertAddressCount(index);
            return base.GetAddress(index);
        }

        public override string GetPrivateKey(int index)
        {
            AssertAddressCount(index);
            return base.GetPrivateKey(index);
        }

        public override PrivateKeyAddressPair GetPrivateKeyAddressPair(int index)
        {
            AssertAddressCount(index);
            return base.GetPrivateKeyAddressPair(index);
        }

        private void AssertAddressCount(int index)
        {
            if (index >= AddressCount)
                throw new IndexOutOfRangeException(
                    $"Value of index is {index}, it cannot be higher than {AddressCount - 1}");
        }
    }
}