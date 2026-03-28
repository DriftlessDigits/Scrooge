using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Memory;
using ECommons.DalamudServices;
using ECommons.EzHookManager;

namespace Scrooge;

/// <summary>
/// Hooks the RetainerHistory processing function to capture sale data.
/// Fires when the game receives the server response after the player
/// views a retainer's Sale History screen.
/// </summary>
internal sealed class RetainerHistoryHook : IDisposable
{
  [StructLayout(LayoutKind.Explicit, Size = 52)]
  public unsafe struct RetainerHistoryData
  {
    [FieldOffset(0)] public uint ItemID;
    [FieldOffset(4)] public uint UnitPrice;          // unit price (confirmed via debug)
    [FieldOffset(8)] public uint UnixTimeSeconds;    // actual sale timestamp from server
    [FieldOffset(12)] public uint Quantity;
    [FieldOffset(16)][MarshalAs(UnmanagedType.I1)] public bool IsHQ;
    [FieldOffset(17)] public byte Unk17;
    [FieldOffset(18)][MarshalAs(UnmanagedType.I1)] public bool IsMannequinn;
    [FieldOffset(19)] private fixed byte _buyerName[32];

    public readonly string BuyerName
    {
      get
      {
        fixed (byte* ptr = _buyerName)
        {
          return MemoryHelper.ReadString((nint)ptr, 32);
        }
      }
    }
  }

  private delegate nint ProcessRetainerHistoryDelegate(nint a1, nint data);

  // EzHook convention: field "ProcessRetainerHistoryHook" → method "ProcessRetainerHistoryDetour"
  [EzHook("40 53 56 57 41 57 48 83 EC 38 48 8B F1")]
  private readonly EzHook<ProcessRetainerHistoryDelegate> ProcessRetainerHistoryHook = null!;

  public RetainerHistoryHook()
  {
    EzSignatureHelper.Initialize(this);
    Svc.Log.Info("[GilTrack] RetainerHistoryHook initialized");
  }

  private unsafe nint ProcessRetainerHistoryDetour(nint a1, nint data)
  {
    try
    {
      var entries = new List<RetainerHistoryData>();

      for (var i = 0; i < 20; i++)
      {
        // Calculate where this record lives in memory
        var offset = 8 + sizeof(RetainerHistoryData) * i;

        // Get a pointer to the struct at that location
        RetainerHistoryData* entryPtr = (RetainerHistoryData*)(data + offset);

        // Read the actual struct from that pointer
        var entry = *entryPtr;


        if (entry.ItemID == 0) 
          break;

        entries.Add(entry);
      }

      if (entries.Count > 0)
      {
        // Use the retainer name set by AutoPinch before clicking Sale History
        var retainerName = GilTracker.CurrentRetainerName;
        GilTracker.ProcessSaleHistory(entries, retainerName);
      }
    }

    catch (Exception ex)
    {
      Svc.Log.Error(ex, "[GilTrack] Error processing retainer history");
    }

    return ProcessRetainerHistoryHook.Original(a1, data);
  }

  public void Dispose()
  {
    ProcessRetainerHistoryHook?.Disable();
  }
}
