using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NegiCraftLauncher.Core.Auth;

/// <summary>
/// Persists the account list. Microsoft accounts round-trip through the same store so the
/// OAuth flow can be added later without changing how accounts are kept.
/// </summary>
public sealed class AccountStore
{
    private sealed class Document
    {
        public List<GameAccount> Accounts { get; set; } = new();
        public string? CurrentId { get; set; }
    }

    private readonly string _path;
    private readonly List<GameAccount> _accounts = new();

    public IReadOnlyList<GameAccount> Accounts => _accounts;
    public GameAccount? Current { get; private set; }

    public AccountStore(string? path = null)
    {
        _path = path ?? NclPaths.AccountsFile;
    }

    public void Load()
    {
        _accounts.Clear();
        Current = null;

        Document? doc = null;
        if (File.Exists(_path))
        {
            try
            {
                doc = NclJson.Deserialize<Document>(File.ReadAllText(_path));
            }
            catch (Exception)
            {
                doc = null;
            }
        }

        if (doc is not null)
        {
            foreach (var account in doc.Accounts.Where(a => !string.IsNullOrWhiteSpace(a.Id)))
            {
                _accounts.Add(account);
            }

            Current = _accounts.FirstOrDefault(a => a.Id == doc.CurrentId);
        }

        if (Current is null)
        {
            Current = _accounts.FirstOrDefault();
        }
    }

    public void Save()
    {
        var doc = new Document { Accounts = _accounts.ToList(), CurrentId = Current?.Id };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, NclJson.Serialize(doc));
    }

    public GameAccount AddOffline(string playerName)
    {
        var existing = _accounts.FirstOrDefault(a =>
            a.Type == GameAccountType.Offline &&
            string.Equals(a.Name, playerName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SetCurrent(existing);
            return existing;
        }

        var account = OfflineAccountFactory.Create(playerName);
        _accounts.Add(account);
        SetCurrent(account);
        Save();
        return account;
    }

    public void Add(GameAccount account)
    {
        _accounts.RemoveAll(a => a.Id == account.Id);
        _accounts.Add(account);
        SetCurrent(account);
        Save();
    }

    public void Remove(GameAccount account)
    {
        if (_accounts.Remove(account))
        {
            if (ReferenceEquals(Current, account))
            {
                Current = _accounts.FirstOrDefault();
            }

            Save();
        }
    }

    public void SetCurrent(GameAccount? account)
    {
        Current = account;
        Save();
    }
}
