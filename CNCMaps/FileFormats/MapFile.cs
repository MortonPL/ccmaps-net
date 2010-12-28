﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using CNCMaps.VirtualFileSystem;
using System.Text.RegularExpressions;
using System.Drawing;
using CNCMaps.MapLogic;
using CNCMaps.Encodings;

namespace CNCMaps.FileFormats {
	class MapFile : IniFile {
		enum MapType {
			RedAlert2,
			YurisRevenge
		}
		MapType mapType = MapType.RedAlert2;

		Tile[,] tiles;
		List<OverlayObject> overlayObjects = new List<OverlayObject>();
		List<SmudgeObject> smudgeObjects = new List<SmudgeObject>();
		List<TerrainObject> terrainObjects = new List<TerrainObject>();
		List<StructureObject> structureObjects = new List<StructureObject>();
		List<InfantryObject> infantryObjects = new List<InfantryObject>();
		List<UnitObject> unitObjects = new List<UnitObject>();
		List<AircraftObject> aircraftObjects = new List<AircraftObject>();

		List<RA2Object> allObjects {
			get {
				var ret = new List<RA2Object>();
				ret.AddRange(this.smudgeObjects);
				ret.AddRange(this.terrainObjects);
				ret.AddRange(this.structureObjects);
				ret.AddRange(this.infantryObjects);
				ret.AddRange(this.unitObjects);
				ret.AddRange(this.aircraftObjects);
				return ret;
			}
		}

		public MapFile(Stream s) : base(s) { }

		internal string DetermineMapName() {
			string infile_nopath = Path.GetFileNameWithoutExtension(this.FileName);

			IniSection Basic = GetSection("Basic");
			if (Basic.ReadBool("Official") == false)
				return Basic.ReadString("Name", infile_nopath);

			var vfs = VirtuaFileSystem.GetInstance();
			string mapext = Path.GetExtension(this.FileName);

			string pktfile;
			bool custom_pkt = false;
			bool isyr = this.mapType == MapType.YurisRevenge;
			string csfEntry;
			string mapName = "";
			PktFile.PktMapEntry mapEntry = null;

			// campaign mission
			if (Basic.ReadBool("MultiplayerOnly") == false && Basic.ReadBool("Official")) {
				MissionsFile mf = new MissionsFile(vfs.Open(isyr ? "missionmd.ini" : "mission.ini"));
				var me = mf.GetMissionEntry(Path.GetFileName(this.FileName));
				csfEntry = me.UIName;
			}
			// multiplayer map
			else {
				if (mapext == ".mmx" || mapext == ".yro") {
					// this file contains the pkt file
					vfs.Add(this.FileName);				
					pktfile = infile_nopath + ".pkt";
					custom_pkt = true;
					if (mapext == ".yro") // definitely YR map
						isyr = true;
				}
				else if (isyr)
					pktfile = "missionsmd.pkt";
				else
					pktfile = "missions.pkt";

				PktFile pkt = new PktFile(vfs.Open(pktfile));
				string pkt_mapname = "";
				if (custom_pkt) 
					pkt_mapname = pkt.MapEntries.First().Key;
				else {
					// fallback for multiplayer maps with, .map extension, 
					// no YR objects so assumed to be ra2, but actually meant to be used on yr
					if (!isyr && mapext == ".map" && !pkt.MapEntries.ContainsKey(infile_nopath) && Basic.ReadBool("MultiplayerOnly")) {
						pktfile = "missionsmd.pkt";
						var pkt_yr = new PktFile(vfs.Open(pktfile));
						if (pkt_yr.MapEntries.ContainsKey(infile_nopath)) {
							isyr = true;
							pkt = pkt_yr;
						}
					}
				}
				// last resort
				if (pkt_mapname == "")
					pkt_mapname = infile_nopath;

				mapEntry = pkt.GetMapEntry(pkt_mapname);
				csfEntry = mapEntry.Description;
			}

			if (csfEntry != "") {
				csfEntry = csfEntry.ToLower();

				string csfFile = isyr ? "ra2md.csf" : "ra2.csf";
				Console.WriteLine("Loading csf file {0}", csfFile);
				CsfFile csf = new CsfFile(vfs.Open(csfFile));
				mapName = csf.GetValue(csfEntry);

				if (mapName.IndexOf(" (") != -1)
					mapName = mapName.Substring(0, mapName.IndexOf(" ("));
								
				if (mapEntry != null) {
					// not standard map
					if ((mapEntry.GameModes & PktFile.GameMode.Standard) == 0) {
						if ((mapEntry.GameModes & PktFile.GameMode.Megawealth) == PktFile.GameMode.Megawealth)
							mapName += " (Megawealth)";
						if ((mapEntry.GameModes & PktFile.GameMode.Duel) == PktFile.GameMode.Duel)
							mapName += " (Land Rush)";
						if ((mapEntry.GameModes & PktFile.GameMode.NavalWar) == PktFile.GameMode.NavalWar)
							mapName += " (Naval War)";
					}
				}
			}
			mapName = MakeValidFileName(mapName);
			Console.WriteLine("Mapname found: {0}", mapName);
			return mapName;
		}

		private static string MakeValidFileName(string name) {
			string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
			string invalidReStr = string.Format(@"[{0}]+", invalidChars);
			return Regex.Replace(name, invalidReStr, "_");
		}

		Rectangle fullSize, localSize;
		EngineType engineType;

		internal void LoadMap(EngineType et = EngineType.AutoDetect) {
			var map = GetSection("Map");
			string[] size = map.ReadString("Size").Split(',');
			fullSize = new Rectangle(int.Parse(size[0]), int.Parse(size[1]), int.Parse(size[2]), int.Parse(size[3]));
			size = map.ReadString("LocalSize").Split(',');
			localSize = new Rectangle(int.Parse(size[0]), int.Parse(size[1]), int.Parse(size[2]), int.Parse(size[3]));

			ReadAllObjects();

			if (et == EngineType.AutoDetect)
				this.engineType = DetectMapType();
			else 
				this.engineType = et;
		}

		private EngineType DetectMapType() {
			if (this.ReadBool("Basic", "RequiredAddon"))
				return EngineType.YurisRevenge;
				
			string theater = this.ReadString("Map", "Theater").ToLower();
			// decision based on theatre
			if (theater == "lunar" || theater == "newurban" || theater == "desert") 
				return EngineType.YurisRevenge;

			// decision based on overlay/trees/structs
			if (!AllObjectsFromRA2()) 
				return EngineType.YurisRevenge;

			// decision based on max tile/threatre
			int maxTileNum = int.MinValue;
			for (int x = 0; x < tiles.GetLength(0); x++)
				for (int y = 0; y < tiles.GetLength(1); y++)
					maxTileNum	= Math.Max(tiles[x, y].TileNum, maxTileNum);
			
			if (theater == "temperate") {
				if (maxTileNum > 838) return EngineType.YurisRevenge;
				return EngineType.RedAlert2;
			}
			else if (theater == "urban") {
				if (maxTileNum > 1077) return EngineType.YurisRevenge;
				return EngineType.RedAlert2;
			}
			else if (theater == "snow") {
				if (maxTileNum > 798) return EngineType.YurisRevenge;
				return EngineType.RedAlert2;
			}
			// decision based on extension
			else if (Path.GetExtension(this.FileName) == ".yrm") return EngineType.YurisRevenge;
			else return EngineType.RedAlert2;			
		}

		private bool AllObjectsFromRA2() {
			return true; // todo
		}

		private void ReadAllObjects() {
			Console.WriteLine("Reading tiles");
			ReadTiles();	
	
			Console.WriteLine("Reading map overlay");
			ReadOverlay();

			Console.WriteLine("Reading map structures");
			ReadStructures();

			Console.WriteLine("Reading map overlay objects");
			ReadTerrain();
	
			Console.WriteLine("Reading map terrain object");
			ReadSmudges();
	
			Console.WriteLine("Reading infantry on map");
			ReadInfantry();
	
			Console.WriteLine("Reading vehicles on map");
			ReadUnits();
	
			Console.WriteLine("Reading aircraft on map");
			ReadAircraft();
		}
				
		private void ReadTiles() {
			var mapSection = GetSection("IsoMapPack5");
			byte[] lzoData = Convert.FromBase64String(mapSection.ConcatenatedValues());
			int cells = (fullSize.Width * 2 - 1) * fullSize.Height;
			int lzoPackSize = cells * 11 + 4; // last 4 bytes contains a lzo pack header saying no more data is left

			byte[] isoMapPack = new byte[lzoPackSize];
			uint total_decompress_size = Format5.DecodeInto(lzoData, isoMapPack);

			tiles = new Tile[fullSize.Width * 2 - 1, fullSize.Height];
			MemoryFile mf = new MemoryFile(isoMapPack);
			int numtiles = 0;
			for (int i = 0; i < cells; i++) {
				ushort rx = mf.ReadUInt16();
				ushort ry = mf.ReadUInt16();
				short tilenum = mf.ReadInt16();
				short zero1 = mf.ReadInt16();
				ushort subtile = mf.ReadByte();
				ushort z = mf.ReadByte();
				byte zero2 = mf.ReadByte();
				
				ushort dx = (ushort)(rx - ry + fullSize.Width - 1);
				ushort dy = (ushort)(rx + ry - fullSize.Width - 1);
				numtiles++;
				this.tiles[dx, dy / 2] = new Tile(dx, dy, rx, ry, tilenum, subtile);
			}
		}
		private void ReadTerrain() {
			IniSection terrainSection = GetSection("Terrain");
			if (terrainSection == null) return;
			foreach (var v in terrainSection.OrderedEntries) {
				int pos = int.Parse(v.Key);
				string name = v.Value;
				int rx = pos % 1000;
				int ry = pos / 1000;
				TerrainObject t = new TerrainObject(name);
				GetTileR(rx, ry).AddObject(t);
				this.terrainObjects.Add(t);
			}
		}
		private void ReadSmudges() {
			IniSection smudgesSection = GetSection("Smudge");
			if (smudgesSection == null) return;
			foreach (var v in smudgesSection.OrderedEntries) {
				string[] entries = v.Value.Split(',');
				string name = entries[0];
				int rx = int.Parse(entries[1]);
				int ry = int.Parse(entries[2]);
				SmudgeObject s = new SmudgeObject(name);
				GetTileR(rx, ry).AddObject(s);
				this.smudgeObjects.Add(s);
			}
		}
		private void ReadOverlay() {
			IniSection overlaySection = GetSection("OverlayPack");
			byte[] format80Data = Convert.FromBase64String(overlaySection.ConcatenatedValues());
			byte[] overlayPack = new byte[1 << 18];
			Format5.DecodeInto(format80Data, overlayPack, 80);

			IniSection overlayDataSection = GetSection("OverlayDataPack");
			format80Data = Convert.FromBase64String(overlayDataSection.ConcatenatedValues());
			byte[] overlayDataPack = new byte[1 << 18];
			Format5.DecodeInto(format80Data, overlayDataPack, 80);

			for (int x = 0; x < tiles.GetLength(0); x++) {
				for (int y = 0; y < tiles.GetLength(1); y++) {
					Tile t = tiles[x, y];
					int idx = t.Rx + 512 * t.Ry;
					byte overlay_id = overlayPack[idx];
					if (overlay_id != 0xFF) {
						byte overlay_value = overlayDataPack[idx];
						OverlayObject ovl = new OverlayObject(overlay_id, overlay_value);
						t.AddObject(ovl);
						this.overlayObjects.Add(ovl);
					}
				}
			}
		}
		private void ReadStructures() {
			IniSection structsSection = GetSection("Structures");
			if (structsSection == null) return;
			foreach (var v in structsSection.OrderedEntries) {
				string[] entries = v.Value.Split(',');
				string owner = entries[0];
				string name = entries[1];
				short health = short.Parse(entries[2]);
				int rx = int.Parse(entries[3]);
				int ry = int.Parse(entries[4]);
				short direction = short.Parse(entries[5]);
				StructureObject s = new StructureObject(owner, name, health, direction);
				GetTileR(rx, ry).AddObject(s);
				this.structureObjects.Add(s);
			}
		}
		private void ReadInfantry() {
			IniSection infantrySection = GetSection("Infantry");
			if (infantrySection == null) return;
			foreach (var v in infantrySection.OrderedEntries) {
				string[] entries = v.Value.Split(',');
				string owner = entries[0];
				string name = entries[1];
				short health = short.Parse(entries[2]);
				int rx = int.Parse(entries[3]);
				int ry = int.Parse(entries[4]);
				short direction = short.Parse(entries[7]);
				InfantryObject i = new InfantryObject(owner, name, health, direction);
				GetTileR(rx, ry).AddObject(i);
				this.infantryObjects.Add(i);
			}
		}
		private void ReadUnits() {
			IniSection unitsSection = GetSection("Units");
			if (unitsSection == null) return;
			foreach (var v in unitsSection.OrderedEntries) {
				string[] entries = v.Value.Split(',');
				string owner = entries[0];
				string name = entries[1];
				short health = short.Parse(entries[2]);
				int rx = int.Parse(entries[3]);
				int ry = int.Parse(entries[4]);
				short direction = short.Parse(entries[5]);
				UnitObject u = new UnitObject(owner, name, health, direction);
				GetTileR(rx, ry).AddObject(u);
				this.unitObjects.Add(u);
			}
		}
		private void ReadAircraft() {
			IniSection aircraftSection = GetSection("Aircraft");
			if (aircraftSection == null) return;
			foreach (var v in aircraftSection.OrderedEntries) {
				string[] entries = v.Value.Split(',');
				string owner = entries[0];
				string name = entries[1];
				short health = short.Parse(entries[2]);
				int rx = int.Parse(entries[3]);
				int ry = int.Parse(entries[4]);
				short direction = short.Parse(entries[5]);
				AircraftObject a = new AircraftObject(owner, name, health, direction);
				GetTileR(rx, ry).AddObject(a);
				this.aircraftObjects.Add(a);
			}
		}

		Tile GetTile(int dx, int dy) {
			return tiles[dx, dy + dx % 2];
		}

		Tile GetTileR(int rx, int ry) {
			int dx = (rx - ry + fullSize.Width - 1);
			int dy = (rx + ry - fullSize.Width - 1) / 2;
			return tiles[dx, dy];
		}
	}
}
