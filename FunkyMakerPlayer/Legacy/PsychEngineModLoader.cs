using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using NVorbis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

namespace FunkyMakerPlayer
{
    /// <summary>
    /// Procesador DIRECTO de mods de Psych Engine
    /// Lee y ejecuta archivos nativos sin conversión previa
    /// Integra: Chart parsing, Sparrow XML, Lua parsing, Audio mixing
    /// </summary>
    public class PsychEngineModLoader
    {
        private Game1 game;
        private string modPath;
        
        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        
        // Datos cargados del mod
        public PsychChart? CurrentChart { get; private set; }
        public PsychStage? CurrentStage { get; private set; }
        public Dictionary<string, PsychCharacter> Characters { get; private set; }
        public Dictionary<string, Texture2D> LoadedSprites { get; private set; }
        public List<LuaSprite> LuaSprites { get; private set; }
        public PsychEvents? CurrentEvents { get; private set; }
        public List<PsychNote> ParsedNotes { get; private set; } = new List<PsychNote>();
        
        // FNMM Integration
        private NoteRenderer? playerNoteRenderer;
        private NoteRenderer? opponentNoteRenderer;
        
        // Audio
        public SoundEffect? InstAudio { get; private set; }
        public SoundEffect? VoicesAudio { get; private set; }
        public SoundEffectInstance? AudioInstance { get; private set; }
        public SoundEffectInstance? VoicesInstance { get; private set; }
        
        // Estado
        public bool IsLoaded { get; private set; } = false;
        public string CurrentSongName { get; private set; } = "";
        
        // Tracking
        private float songPosition = 0f;
        private int currentStep = 0;
        private int currentBeat = 0;
        
        // Combo bounce animation (FNF style)
        private int lastCombo = 0;
        private float comboScale = 1.0f;
        private const float COMBO_BOUNCE_SCALE = 1.2f;  // Scale when combo increases
        private const float COMBO_LERP_SPEED = 12f;     // How fast it returns to normal
        
        public PsychEngineModLoader(Game1 gameInstance, string psychModPath)
        {
            game = gameInstance;
            modPath = psychModPath;
            Characters = new Dictionary<string, PsychCharacter>();
            LoadedSprites = new Dictionary<string, Texture2D>();
            LuaSprites = new List<LuaSprite>();
            
            Console.WriteLine($"[*] Psych Engine Mod Loader inicializado");
            Console.WriteLine($"[>] Mod path: {modPath}");
        }
        
        /// <summary>
        /// Detecta y carga automáticamente un mod de Psych Engine
        /// </summary>
        public static PsychEngineModLoader? TryLoad(Game1 game, string path)
        {
            if (!Directory.Exists(path)) return null;
            
            string dataDir = Path.Combine(path, "data");
            if (!Directory.Exists(dataDir)) return null;
            
            // Buscar primera canción
            foreach (var songDir in Directory.EnumerateDirectories(dataDir))
            {
                string songName = Path.GetFileName(songDir);
                string chartPath = Path.Combine(songDir, songName.ToLower() + ".json");
                
                if (File.Exists(chartPath))
                {
                    var loader = new PsychEngineModLoader(game, path);
                    if (loader.LoadSong(songName))
                    {
                        return loader;
                    }
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Carga una canción completa del mod
        /// </summary>
        public bool LoadSong(string songName)
        {
            Console.WriteLine($"\n[♪] Cargando {songName}...");
            Console.WriteLine(new string('=', 60));
            
            try
            {
                // 1. Cargar chart (.json)
                Console.WriteLine("📊 Cargando chart...");
                if (!LoadChart(songName))
                {
                    Console.WriteLine("❌ Error cargando chart");
                    return false;
                }
                
                // 2. Cargar metadata
                Console.WriteLine("📝 Cargando metadata...");
                LoadMetadata(songName);
                
                // 3. Cargar eventos
                Console.WriteLine("⚡ Cargando eventos...");
                LoadEvents(songName);
                
                // 4. Cargar stage
                Console.WriteLine("🎭 Cargando stage...");
                LoadStage(songName);
                
                // 5. Cargar personajes
                Console.WriteLine("👤 Cargando personajes...");
                LoadCharacters();
                
                // 6. Preparar audio (Inst + Voices)
                Console.WriteLine("[♪] Preparando audio...");
                if (!PrepareAudio(songName))
                {
                    Console.WriteLine("⚠️ Audio no disponible");
                }
                
                // 7. Cargar sprites necesarios
                Console.WriteLine("🖼️ Cargando sprites...");
                LoadSprites(songName);
                
                Console.WriteLine(new string('=', 60));
                Console.WriteLine($"✅ {songName} cargado exitosamente!");
                IsLoaded = true;
                CurrentSongName = songName;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                Console.WriteLine($"   Stack: {ex.StackTrace}");
                return false;
            }
        }
        
        /// <summary>
        /// Inicializa los NoteRenderers usando el sistema FNMM
        /// </summary>
        public void SetupNoteRenderers(GraphicsDevice gd, Texture2D pixel)
        {
            if (CurrentChart == null || ParsedNotes.Count == 0) return;
            
            try
            {
                // Crear FnmmLevel para jugador
                var playerLevel = new FnmmLevel
                {
                    LevelName = CurrentSongName,
                    Data = new FnmmData
                    {
                        Bpm = CurrentChart.Bpm,
                        Speed = CurrentChart.Speed,
                        Strumlines = new List<List<List<FnmmNote>>>()
                    }
                };
                
                // Crear FnmmLevel para oponente
                var opponentLevel = new FnmmLevel
                {
                    LevelName = CurrentSongName,
                    Data = new FnmmData
                    {
                        Bpm = CurrentChart.Bpm,
                        Speed = CurrentChart.Speed,
                        Strumlines = new List<List<List<FnmmNote>>>()
                    }
                };
                
                // Crear strumline con 4 lanes para jugador
                var playerStrumline = new List<List<FnmmNote>>();
                for (int i = 0; i < 4; i++)
                {
                    playerStrumline.Add(new List<FnmmNote>());
                }
                
                // Crear strumline con 4 lanes para oponente
                var opponentStrumline = new List<List<FnmmNote>>();
                for (int i = 0; i < 4; i++)
                {
                    opponentStrumline.Add(new List<FnmmNote>());
                }
                
                // Convertir notas de Psych a FNMM format y distribuir por lane
                foreach (var psychNote in ParsedNotes)
                {
                    // Convertir tiempo de ms a beats
                    float timeInBeats = (psychNote.Time / 1000f) / 60f * CurrentChart.Bpm;
                    float durationInBeats = (psychNote.Length / 1000f) / 60f * CurrentChart.Bpm;
                    
                    var fnmmNote = new FnmmNote
                    {
                        Start = timeInBeats,
                        Duration = durationInBeats,
                        Props = new NoteProps
                        {
                            Skin = 0 // Normal note
                        }
                    };
                    
                    // Agregar a la lane correcta según si es jugador u oponente
                    if (psychNote.MustHitNote)
                    {
                        playerStrumline[psychNote.Direction].Add(fnmmNote);
                    }
                    else
                    {
                        opponentStrumline[psychNote.Direction].Add(fnmmNote);
                    }
                }
                
                // Agregar strumlines a los levels
                playerLevel.Data.Strumlines.Add(playerStrumline);
                opponentLevel.Data.Strumlines.Add(opponentStrumline);
                
                // Crear NoteRenderers con posiciones por defecto
                int screenWidth = 1280; // Default, se ajustará después
                float playerX = screenWidth / 2f - (NoteRenderer.LANE_WIDTH * 2);
                float opponentX = screenWidth / 2f - (NoteRenderer.LANE_WIDTH * 6);
                
                playerNoteRenderer = new NoteRenderer(playerLevel, 0, playerX, gd, null);
                opponentNoteRenderer = new NoteRenderer(opponentLevel, 1, opponentX, gd, null);
                
                int playerCount = playerStrumline.Sum(lane => lane.Count);
                int opponentCount = opponentStrumline.Sum(lane => lane.Count);
                
                Console.WriteLine($"✅ NoteRenderers creados: {playerCount} notas jugador, {opponentCount} notas oponente");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error creando NoteRenderers: {ex.Message}");
                Console.WriteLine($"   Stack: {ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Carga el chart de Psych Engine
        /// </summary>
        private bool LoadChart(string songName)
        {
            string dataPath = Path.Combine(modPath, "data", songName.ToLower());
            
            // Buscar archivo de chart (prioridad: normal, hard, easy)
            string[] chartFiles = { 
                $"{songName.ToLower()}.json",
                $"{songName.ToLower()}-hard.json",
                $"{songName.ToLower()}-normal.json",
                $"{songName.ToLower()}-easy.json"
            };
            
            foreach (var file in chartFiles)
            {
                string fullPath = Path.Combine(dataPath, file);
                if (File.Exists(fullPath))
                {
                    try
                    {
                        string jsonText = File.ReadAllText(fullPath);
                        CurrentChart = ParsePsychChart(jsonText);
                        Console.WriteLine($"  [OK] Chart: {file}");
                        Console.WriteLine($"  [OK] BPM: {CurrentChart.Bpm}");
                        Console.WriteLine($"  [OK] Secciones: {CurrentChart.Notes?.Count ?? 0}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  ⚠️ Error parseando {file}: {ex.Message}");
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Parsea el chart de Psych Engine
        /// </summary>
        private PsychChart ParsePsychChart(string json)
        {
            var doc = JsonNode.Parse(json)!.AsObject();
            
            // Algunos charts envuelven todo en "song"
            if (doc.ContainsKey("song") && doc["song"] is JsonObject songObj)
            {
                var flat = songObj.ToJsonString();
                var chart = JsonSerializer.Deserialize<PsychChart>(flat, _json) ?? new PsychChart();
                ParseNotes(chart);
                return chart;
            }
            
            // Formato plano
            var result = JsonSerializer.Deserialize<PsychChart>(json, _json) ?? new PsychChart();
            ParseNotes(result);
            return result;
        }
        
        /// <summary>
        /// Parsea las notas del chart en una lista plana
        /// </summary>
        private void ParseNotes(PsychChart chart)
        {
            ParsedNotes.Clear();
            
            if (chart.Notes == null) return;
            
            foreach (var section in chart.Notes)
            {
                if (section.SectionNotes == null) continue;
                
                foreach (var noteElement in section.SectionNotes)
                {
                    try
                    {
                        if (noteElement.ValueKind != JsonValueKind.Array) continue;
                        
                        var noteArray = noteElement.EnumerateArray().ToList();
                        if (noteArray.Count < 3) continue;
                        
                        float time = noteArray[0].GetSingle();
                        int direction = noteArray[1].GetInt32();
                        float length = noteArray[2].GetSingle();
                        
                        // Determinar si es nota del jugador
                        // En Psych Engine: 0-3 = opponent, 4-7 = player (en secciones normales)
                        // Pero si mustHitSection es true, se invierte
                        bool isPlayerNote = section.MustHitSection ? (direction < 4) : (direction >= 4);
                        
                        // Normalizar dirección a 0-3
                        int normalizedDir = direction % 4;
                        
                        // Tipo de nota (opcional, 4to elemento)
                        string noteType = "";
                        if (noteArray.Count > 3 && noteArray[3].ValueKind == JsonValueKind.String)
                        {
                            noteType = noteArray[3].GetString() ?? "";
                        }
                        
                        ParsedNotes.Add(new PsychNote
                        {
                            Time = time,
                            Direction = normalizedDir,
                            Length = length,
                            MustHitNote = isPlayerNote,
                            Type = noteType
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  ⚠️ Error parseando nota: {ex.Message}");
                    }
                }
            }
            
            // Ordenar por tiempo
            ParsedNotes.Sort((a, b) => a.Time.CompareTo(b.Time));
            
            Console.WriteLine($"  ✓ {ParsedNotes.Count} notas parseadas ({ParsedNotes.Count(n => n.MustHitNote)} del jugador)");
        }
        
        /// <summary>
        /// Carga metadata adicional
        /// </summary>
        private void LoadMetadata(string songName)
        {
            string metaPath = Path.Combine(modPath, "data", songName.ToLower(), "metadata.json");
            
            if (File.Exists(metaPath))
            {
                try
                {
                    string jsonText = File.ReadAllText(metaPath);
                    Console.WriteLine($"  ✓ Metadata cargada");
                }
                catch { }
            }
        }
        
        /// <summary>
        /// Carga eventos del chart
        /// </summary>
        private void LoadEvents(string songName)
        {
            string eventsPath = Path.Combine(modPath, "data", songName.ToLower(), "events.json");
            
            if (File.Exists(eventsPath))
            {
                try
                {
                    string jsonText = File.ReadAllText(eventsPath);
                    CurrentEvents = JsonSerializer.Deserialize<PsychEvents>(jsonText, _json);
                    Console.WriteLine($"  ✓ Eventos cargados: {CurrentEvents?.Events?.Count ?? 0}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ⚠️ Error cargando eventos: {ex.Message}");
                }
            }
            
            // También buscar events.lua para scripts personalizados
            string eventsLuaPath = Path.Combine(modPath, "data", songName.ToLower(), "events.lua");
            if (File.Exists(eventsLuaPath))
            {
                Console.WriteLine($"  ✓ Script de eventos Lua encontrado");
                // TODO: Implementar ejecución de scripts Lua si es necesario
            }
        }
        
        /// <summary>
        /// Carga el stage
        /// </summary>
        private void LoadStage(string songName)
        {
            // Primero intentar stage específico del chart
            string stageName = CurrentChart?.Stage ?? "stage";
            string stagePath = Path.Combine(modPath, "stages", $"{stageName}.json");
            
            if (!File.Exists(stagePath))
            {
                // Fallback a stage por defecto
                stagePath = Path.Combine(modPath, "stages", "bfbg.json");
            }
            
            if (File.Exists(stagePath))
            {
                try
                {
                    string jsonText = File.ReadAllText(stagePath);
                    CurrentStage = ParsePsychStage(jsonText);
                    Console.WriteLine($"  ✓ Stage: {stageName}");
                    
                    // Cargar Lua script del stage si existe
                    string luaPath = Path.Combine(modPath, "stages", $"{stageName}.lua");
                    if (File.Exists(luaPath))
                    {
                        string luaScript = File.ReadAllText(luaPath);
                        LuaSprites = ParseLuaScript(luaScript);
                        Console.WriteLine($"  ✓ Stage Lua: {LuaSprites.Count} sprites");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ⚠️ Error cargando stage: {ex.Message}");
                    CurrentStage = CreateDefaultStage();
                }
            }
            else
            {
                CurrentStage = CreateDefaultStage();
            }
        }
        
        /// <summary>
        /// Parsea un stage de Psych Engine
        /// </summary>
        private PsychStage ParsePsychStage(string json)
        {
            return JsonSerializer.Deserialize<PsychStage>(json, _json) ?? new PsychStage();
        }
        
        /// <summary>
        /// Crea un stage por defecto
        /// </summary>
        private PsychStage CreateDefaultStage()
        {
            return new PsychStage
            {
                DefaultZoom = 1.0f,
                Boyfriend = new List<float> { 760f, 440f },
                Opponent = new List<float> { -40f, 510f },
                Girlfriend = new List<float> { 310f, 150f }
            };
        }
        
        /// <summary>
        /// Carga los personajes necesarios
        /// </summary>
        private void LoadCharacters()
        {
            if (CurrentChart == null) return;
            
            // Cargar BF
            if (!string.IsNullOrEmpty(CurrentChart.Player1))
                LoadCharacter(CurrentChart.Player1, "bf");
            
            // Cargar Dad/Opponent
            if (!string.IsNullOrEmpty(CurrentChart.Player2))
                LoadCharacter(CurrentChart.Player2, "dad");
            
            // Cargar GF
            if (!string.IsNullOrEmpty(CurrentChart.GfVersion))
            {
                LoadCharacter(CurrentChart.GfVersion, "gf");
            }
            
            Console.WriteLine($"  ✓ {Characters.Count} personajes cargados");
        }
        
        /// <summary>
        /// Carga un personaje individual
        /// </summary>
        private void LoadCharacter(string charName, string role)
        {
            string charPath = Path.Combine(modPath, "characters", $"{charName}.json");
            
            if (File.Exists(charPath))
            {
                try
                {
                    string jsonText = File.ReadAllText(charPath);
                    var character = ParsePsychCharacter(jsonText, charName);
                    Characters[role] = character;
                    Console.WriteLine($"    ✓ {role}: {charName}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    ⚠️ Error cargando {charName}: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Parsea un personaje de Psych Engine
        /// </summary>
        private PsychCharacter ParsePsychCharacter(string json, string name)
        {
            return JsonSerializer.Deserialize<PsychCharacter>(json, _json) ?? new PsychCharacter();
        }
        
        /// <summary>
        /// Prepara el audio mezclando Inst + Voices
        /// </summary>
        private bool PrepareAudio(string songName)
        {
            string songsPath = Path.Combine(modPath, "songs", songName);
            string instPath = Path.Combine(songsPath, "Inst.ogg");
            string voicesPath = Path.Combine(songsPath, "Voices.ogg");
            
            if (!File.Exists(instPath))
            {
                Console.WriteLine("  ⚠️ Inst.ogg no encontrado");
                return false;
            }
            
            try
            {
                // Cargar Inst
                InstAudio = LoadOggAsSoundEffect(instPath);
                Console.WriteLine("  ✓ Inst.ogg cargado");
                
                // Si hay Voices, cargar también
                if (File.Exists(voicesPath))
                {
                    VoicesAudio = LoadOggAsSoundEffect(voicesPath);
                    Console.WriteLine("  ✓ Voices.ogg cargado");
                    Console.WriteLine("  ℹ️ Nota: Inst y Voices se reproducirán por separado");
                }
                else
                {
                    Console.WriteLine("  ℹ️ Solo Inst (sin Voices)");
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠️ Error preparando audio: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Carga un archivo OGG como SoundEffect
        /// </summary>
        private SoundEffect LoadOggAsSoundEffect(string oggPath)
        {
            using (var vorbis = new VorbisReader(oggPath))
            {
                // Leer todos los samples
                var buffer = new float[vorbis.TotalSamples * vorbis.Channels];
                vorbis.ReadSamples(buffer, 0, buffer.Length);
                
                // Convertir float a short (16-bit PCM)
                var shortBuffer = new short[buffer.Length];
                for (int i = 0; i < buffer.Length; i++)
                {
                    shortBuffer[i] = (short)(buffer[i] * short.MaxValue);
                }
                
                // Convertir short a byte
                var byteBuffer = new byte[shortBuffer.Length * 2];
                Buffer.BlockCopy(shortBuffer, 0, byteBuffer, 0, byteBuffer.Length);
                
                // Crear SoundEffect
                return new SoundEffect(
                    byteBuffer,
                    vorbis.SampleRate,
                    (AudioChannels)vorbis.Channels
                );
            }
        }
        
        /// <summary>
        /// Carga sprites necesarios para el nivel
        /// </summary>
        private void LoadSprites(string songName)
        {
            string imagesPath = Path.Combine(modPath, "images");
            
            // Cargar sprites de personajes
            foreach (var character in Characters.Values)
            {
                LoadSpriteTexture(character.Image);
            }
            
            // Cargar sprites de stage
            string stagesPath = Path.Combine(imagesPath, "stages");
            if (Directory.Exists(stagesPath))
            {
                // Cargar sprites específicos del stage
                // TODO: Implementar según stage.lua
            }
            
            Console.WriteLine($"  ✓ {LoadedSprites.Count} sprites cargados");
        }
        
        /// <summary>
        /// Carga una textura de sprite
        /// </summary>
        private void LoadSpriteTexture(string spritePath)
        {
            if (string.IsNullOrEmpty(spritePath))
                return;
                
            if (LoadedSprites.ContainsKey(spritePath))
                return;
            
            string fullPath = Path.Combine(modPath, "images", $"{spritePath}.png");
            
            if (File.Exists(fullPath))
            {
                try
                {
                    using (var stream = File.OpenRead(fullPath))
                    {
                        var texture = Texture2D.FromStream(game.GraphicsDevice, stream);
                        LoadedSprites[spritePath] = texture;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    ⚠️ Error cargando {spritePath}: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"    ⚠️ Sprite no encontrado: {fullPath}");
            }
        }
        
        /// <summary>
        /// Inicia la reproducción del audio
        /// </summary>
        public void PlayAudio()
        {
            if (InstAudio == null)
            {
                Console.WriteLine("⚠️ No hay audio para reproducir");
                return;
            }
            
            try
            {
                // Crear instancias de audio
                AudioInstance = InstAudio.CreateInstance();
                
                if (VoicesAudio != null)
                {
                    VoicesInstance = VoicesAudio.CreateInstance();
                }
                
                // Reproducir ambos tracks al mismo tiempo para evitar desync
                // Llamar Play() en secuencia inmediata minimiza el delay
                AudioInstance.Play();
                VoicesInstance?.Play();
                
                Console.WriteLine("▶️ Audio iniciado (Inst + Voices sincronizados)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error reproduciendo audio: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Detiene la reproducción del audio
        /// </summary>
        public void StopAudio()
        {
            try
            {
                AudioInstance?.Stop();
                VoicesInstance?.Stop();
                Console.WriteLine("⏹️ Audio detenido");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error deteniendo audio: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Actualiza el estado del mod (llamar cada frame)
        /// </summary>
        public void Update(GameTime gameTime, float songTime, bool started)
        {
            if (CurrentChart == null) return;
            
            if (!started) return;
            
            // Actualizar posición de la canción
            songPosition = songTime * 1000f; // Convert to milliseconds
            
            // Calcular step y beat
            float stepCrochet = (float)((60.0 / CurrentChart.Bpm) * 1000.0 / 4.0);
            int newStep = (int)(songPosition / stepCrochet);
            
            if (newStep != currentStep)
            {
                currentStep = newStep;
                OnStepHit(currentStep);
            }
            
            int newBeat = currentStep / 4;
            if (newBeat != currentBeat)
            {
                currentBeat = newBeat;
                OnBeatHit(currentBeat);
            }
            
            // Combo bounce animation (FNF style)
            float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
            if (playerNoteRenderer != null)
            {
                int currentCombo = playerNoteRenderer.Combo;
                
                // Detectar cuando el combo aumenta
                if (currentCombo > lastCombo)
                {
                    comboScale = COMBO_BOUNCE_SCALE;  // Salta a 1.2
                }
                lastCombo = currentCombo;
                
                // Lerp suave de vuelta a 1.0
                comboScale = MathHelper.Lerp(comboScale, 1.0f, COMBO_LERP_SPEED * dt);
            }
            
            // NoteRenderer maneja las notas automáticamente
        }
        
        /// <summary>
        /// Evento cuando se alcanza un step
        /// </summary>
        private void OnStepHit(int step)
        {
            // Aquí se pueden procesar eventos específicos del step
            // Por ejemplo, ejecutar scripts Lua del mod
            ProcessEventsAtStep(step);
        }
        
        /// <summary>
        /// Evento cuando se alcanza un beat
        /// </summary>
        private void OnBeatHit(int beat)
        {
            // Aquí se pueden procesar eventos de beat
            // Por ejemplo, hacer que los personajes bailen
            ProcessEventsAtBeat(beat);
        }
        
        /// <summary>
        /// Procesa eventos que ocurren en un step específico
        /// </summary>
        private void ProcessEventsAtStep(int step)
        {
            if (CurrentEvents?.Events == null) return;
            
            float stepTime = step * (float)((60.0 / CurrentChart.Bpm) * 1000.0 / 4.0);
            
            foreach (var eventElement in CurrentEvents.Events)
            {
                if (eventElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                    continue;
                
                var eventArray = eventElement.EnumerateArray().ToList();
                if (eventArray.Count < 2) continue;
                
                float eventTime = eventArray[0].GetSingle();
                
                // Verificar si el evento ocurre en este step (con tolerancia de 50ms)
                if (Math.Abs(eventTime - stepTime) <= 50f)
                {
                    ProcessEvent(eventArray);
                }
            }
        }
        
        /// <summary>
        /// Procesa eventos que ocurren en un beat específico
        /// </summary>
        private void ProcessEventsAtBeat(int beat)
        {
            // Eventos de beat (como zoom de cámara, etc.)
            Console.WriteLine($"[Beat {beat}]");
        }
        
        /// <summary>
        /// Procesa un evento individual
        /// </summary>
        private void ProcessEvent(List<System.Text.Json.JsonElement> eventData)
        {
            if (eventData.Count < 2) return;
            
            // eventData[0] = tiempo
            // eventData[1] = array de [nombre, valor1, valor2]
            
            if (eventData[1].ValueKind != System.Text.Json.JsonValueKind.Array)
                return;
            
            var eventDetails = eventData[1].EnumerateArray().ToList();
            if (eventDetails.Count == 0) return;
            
            string eventName = eventDetails[0].GetString() ?? "";
            string value1 = eventDetails.Count > 1 ? eventDetails[1].GetString() ?? "" : "";
            string value2 = eventDetails.Count > 2 ? eventDetails[2].GetString() ?? "" : "";
            
            Console.WriteLine($"[Event] {eventName} ({value1}, {value2})");
            
            // Procesar eventos comunes de Psych Engine
            switch (eventName.ToLower())
            {
                case "add camera zoom":
                case "new add camera zoom":
                    // TODO: Implementar zoom de cámara
                    break;
                    
                case "camera_target":
                    // TODO: Cambiar objetivo de cámara
                    break;
                    
                case "change character":
                    // TODO: Cambiar personaje
                    break;
                    
                case "play animation":
                    // TODO: Reproducir animación
                    break;
                    
                case "hud":
                    // TODO: Mostrar/ocultar HUD
                    break;
                    
                default:
                    // Evento personalizado
                    break;
            }
        }
        
        /// <summary>
        /// Obtiene las notas que deben aparecer en el tiempo actual
        /// </summary>
        public List<PsychNote> GetNotesAtTime(float time, float window = 100f)
        {
            var notes = new List<PsychNote>();
            
            if (CurrentChart?.Notes == null) return notes;
            
            // Recorrer todas las secciones y extraer notas
            float currentTime = 0f;
            float crochet = (float)((60.0 / CurrentChart.Bpm) * 1000.0); // ms por beat
            
            foreach (var section in CurrentChart.Notes)
            {
                if (section.SectionNotes == null) continue;
                
                float sectionBpm = section.ChangeBpm ? section.Bpm : CurrentChart.Bpm;
                float sectionCrochet = (float)((60.0 / sectionBpm) * 1000.0);
                float sectionLength = sectionCrochet * section.SectionBeats;
                
                // Parsear notas de esta sección
                foreach (var noteElement in section.SectionNotes)
                {
                    if (noteElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                        continue;
                    
                    var noteArray = noteElement.EnumerateArray().ToList();
                    if (noteArray.Count < 2) continue;
                    
                    float noteTime = noteArray[0].GetSingle();
                    int noteData = noteArray[1].GetInt32();
                    float noteLength = noteArray.Count > 2 ? noteArray[2].GetSingle() : 0f;
                    string noteType = noteArray.Count > 3 ? noteArray[3].GetString() ?? "" : "";
                    
                    // Verificar si la nota está en el rango de tiempo
                    if (Math.Abs(noteTime - time) <= window)
                    {
                        notes.Add(new PsychNote
                        {
                            Time = noteTime,
                            Direction = noteData % 4, // 0-3 para las 4 direcciones
                            Length = noteLength,
                            Type = noteType,
                            MustHitNote = section.MustHitSection ? (noteData < 4) : (noteData >= 4)
                        });
                    }
                }
                
                currentTime += sectionLength;
            }
            
            return notes;
        }
        
        /// <summary>
        /// Obtiene todas las notas del chart en orden cronológico
        /// </summary>
        public List<PsychNote> GetAllNotes()
        {
            var allNotes = new List<PsychNote>();
            
            if (CurrentChart?.Notes == null) return allNotes;
            
            foreach (var section in CurrentChart.Notes)
            {
                if (section.SectionNotes == null) continue;
                
                foreach (var noteElement in section.SectionNotes)
                {
                    if (noteElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                        continue;
                    
                    var noteArray = noteElement.EnumerateArray().ToList();
                    if (noteArray.Count < 2) continue;
                    
                    float noteTime = noteArray[0].GetSingle();
                    int noteData = noteArray[1].GetInt32();
                    float noteLength = noteArray.Count > 2 ? noteArray[2].GetSingle() : 0f;
                    string noteType = noteArray.Count > 3 ? noteArray[3].GetString() ?? "" : "";
                    
                    allNotes.Add(new PsychNote
                    {
                        Time = noteTime,
                        Direction = noteData % 4,
                        Length = noteLength,
                        Type = noteType,
                        MustHitNote = section.MustHitSection ? (noteData < 4) : (noteData >= 4)
                    });
                }
            }
            
            // Ordenar por tiempo
            allNotes.Sort((a, b) => a.Time.CompareTo(b.Time));
            
            return allNotes;
        }
        
        #region Sparrow XML Parser
        
        /// <summary>
        /// Parsea un archivo Sparrow XML Atlas
        /// </summary>
        private List<SparrowFrame> ParseSparrowXml(string xml)
        {
            var frames = new List<SparrowFrame>();
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml);
                var nodes = doc.SelectNodes("//SubTexture");
                if (nodes == null) return frames;
                
                foreach (XmlNode node in nodes)
                {
                    if (node.Attributes == null) continue;
                    
                    int fw = int.TryParse(node.Attributes["frameWidth"]?.Value, out int fw_) ? fw_ : 0;
                    int fh = int.TryParse(node.Attributes["frameHeight"]?.Value, out int fh_) ? fh_ : 0;
                    
                    frames.Add(new SparrowFrame
                    {
                        Name = node.Attributes["name"]?.Value ?? "",
                        X = int.TryParse(node.Attributes["x"]?.Value, out int x) ? x : 0,
                        Y = int.TryParse(node.Attributes["y"]?.Value, out int y) ? y : 0,
                        Width = int.TryParse(node.Attributes["width"]?.Value, out int w) ? w : 0,
                        Height = int.TryParse(node.Attributes["height"]?.Value, out int h) ? h : 0,
                        FrameX = int.TryParse(node.Attributes["frameX"]?.Value, out int fx) ? fx : 0,
                        FrameY = int.TryParse(node.Attributes["frameY"]?.Value, out int fy) ? fy : 0,
                        FrameWidth = fw > 0 ? fw : (int.TryParse(node.Attributes["width"]?.Value, out int fw2) ? fw2 : 0),
                        FrameHeight = fh > 0 ? fh : (int.TryParse(node.Attributes["height"]?.Value, out int fh2) ? fh2 : 0),
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sparrow XML] Error: {ex.Message}");
            }
            return frames;
        }
        
        #endregion
        
        #region Lua Parser
        
        /// <summary>
        /// Parsea un script Lua de stage y extrae sprites
        /// </summary>
        private List<LuaSprite> ParseLuaScript(string lua)
        {
            var sprites = new Dictionary<string, LuaSprite>(StringComparer.OrdinalIgnoreCase);
            var addOrder = new List<string>();
            
            // Strip comments
            lua = System.Text.RegularExpressions.Regex.Replace(lua, @"--[^\n]*", "");
            lua = System.Text.RegularExpressions.Regex.Replace(lua, @"--\[\[.*?\]\]", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            
            // makeAnimatedLuaSprite(tag, image, x, y)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(lua,
                @"makeAnimatedLuaSprite\s*\(\s*(['""][^'""]*['""])\s*,\s*(['""][^'""]*['""])\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string tag = m.Groups[1].Value.Trim().Trim('\'', '"');
                string image = m.Groups[2].Value.Trim().Trim('\'', '"');
                float x = float.Parse(m.Groups[3].Value);
                float y = float.Parse(m.Groups[4].Value);
                sprites[tag] = new LuaSprite { Tag = tag, Image = image, X = x, Y = y, IsAnimated = true };
            }
            
            // makeLuaSprite(tag, image, x, y)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(lua,
                @"makeLuaSprite\s*\(\s*(['""][^'""]*['""])\s*,\s*(['""][^'""]*['""])\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string tag = m.Groups[1].Value.Trim().Trim('\'', '"');
                string image = m.Groups[2].Value.Trim().Trim('\'', '"');
                float x = float.Parse(m.Groups[3].Value);
                float y = float.Parse(m.Groups[4].Value);
                sprites[tag] = new LuaSprite { Tag = tag, Image = image, X = x, Y = y, IsAnimated = false };
            }
            
            // setScrollFactor(tag, x, y)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(lua,
                @"setScrollFactor\s*\(\s*(['""][^'""]*['""])\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string tag = m.Groups[1].Value.Trim().Trim('\'', '"');
                float scrollX = float.Parse(m.Groups[2].Value);
                float scrollY = float.Parse(m.Groups[3].Value);
                
                if (sprites.TryGetValue(tag, out var spr))
                {
                    spr.ScrollFactorX = scrollX;
                    spr.ScrollFactorY = scrollY;
                }
            }
            
            // scaleObject(tag, scaleX, scaleY)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(lua,
                @"scaleObject\s*\(\s*(['""][^'""]*['""])\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string tag = m.Groups[1].Value.Trim().Trim('\'', '"');
                float scaleX = float.Parse(m.Groups[2].Value);
                float scaleY = float.Parse(m.Groups[3].Value);
                
                if (sprites.TryGetValue(tag, out var spr))
                {
                    spr.ScaleX = scaleX;
                    spr.ScaleY = scaleY;
                }
            }
            
            // setProperty(tag, 'alpha', value)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(lua,
                @"setProperty\s*\(\s*(['""][^'""]*['""])\s*,\s*['""]alpha['""]\s*,\s*(-?\d+(?:\.\d+)?)\s*\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string tag = m.Groups[1].Value.Trim().Trim('\'', '"');
                float alpha = float.Parse(m.Groups[2].Value);
                
                if (sprites.TryGetValue(tag, out var spr))
                {
                    spr.Alpha = alpha;
                }
            }
            
            // addLuaSprite(tag, isBehind)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(lua,
                @"addLuaSprite\s*\(\s*(['""][^'""]*['""])\s*(?:,\s*(true|false)\s*)?\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string tag = m.Groups[1].Value.Trim().Trim('\'', '"');
                bool isBehind = m.Groups[2].Success && m.Groups[2].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                
                if (sprites.TryGetValue(tag, out var spr))
                {
                    spr.IsBehind = isBehind;
                    if (!addOrder.Contains(tag))
                        addOrder.Add(tag);
                }
            }
            
            var result = new List<LuaSprite>();
            foreach (var tag in addOrder)
                if (sprites.TryGetValue(tag, out var spr))
                    result.Add(spr);
            
            return result;
        }
        
        #endregion
        
        #region Sprite Processing
        
        /// <summary>
        /// Carga un Sparrow atlas completo (PNG + XML)
        /// </summary>
        public Dictionary<string, SparrowFrame> LoadSparrowAtlas(string atlasName)
        {
            var frames = new Dictionary<string, SparrowFrame>();
            
            string pngPath = Path.Combine(modPath, "images", $"{atlasName}.png");
            string xmlPath = Path.Combine(modPath, "images", $"{atlasName}.xml");
            
            if (!File.Exists(pngPath) || !File.Exists(xmlPath))
            {
                Console.WriteLine($"⚠️ Atlas incompleto: {atlasName}");
                return frames;
            }
            
            try
            {
                // Cargar textura base
                LoadSpriteTexture(atlasName);
                
                // Parsear XML
                string xmlContent = File.ReadAllText(xmlPath);
                var frameList = ParseSparrowXml(xmlContent);
                
                foreach (var frame in frameList)
                {
                    frames[frame.Name] = frame;
                }
                
                Console.WriteLine($"  ✓ Atlas {atlasName}: {frames.Count} frames");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠️ Error cargando atlas {atlasName}: {ex.Message}");
            }
            
            return frames;
        }
        
        /// <summary>
        /// Extrae un frame específico de un Sparrow atlas
        /// </summary>
        public Texture2D? CropSparrowFrame(string atlasName, SparrowFrame frame)
        {
            if (!LoadedSprites.TryGetValue(atlasName, out var atlasTexture))
            {
                Console.WriteLine($"⚠️ Atlas no cargado: {atlasName}");
                return null;
            }
            
            try
            {
                // Crear nueva textura con el tamaño del frame
                var croppedTexture = new Texture2D(game.GraphicsDevice, frame.Width, frame.Height);
                
                // Extraer datos del atlas
                Color[] atlasData = new Color[atlasTexture.Width * atlasTexture.Height];
                atlasTexture.GetData(atlasData);
                
                // Copiar región específica
                Color[] frameData = new Color[frame.Width * frame.Height];
                for (int y = 0; y < frame.Height; y++)
                {
                    for (int x = 0; x < frame.Width; x++)
                    {
                        int atlasIndex = (frame.Y + y) * atlasTexture.Width + (frame.X + x);
                        int frameIndex = y * frame.Width + x;
                        
                        if (atlasIndex >= 0 && atlasIndex < atlasData.Length)
                        {
                            frameData[frameIndex] = atlasData[atlasIndex];
                        }
                    }
                }
                
                croppedTexture.SetData(frameData);
                return croppedTexture;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error cropping frame: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Obtiene todos los frames de una animación específica
        /// </summary>
        public List<SparrowFrame> GetAnimationFrames(Dictionary<string, SparrowFrame> atlas, string animPrefix)
        {
            var frames = new List<SparrowFrame>();
            
            foreach (var kvp in atlas)
            {
                if (kvp.Key.StartsWith(animPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    frames.Add(kvp.Value);
                }
            }
            
            // Ordenar por nombre (usualmente tienen números al final)
            frames.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            
            return frames;
        }
        
        #endregion
        
        #region Audio Mixing
        
        /// <summary>
        /// Mezcla múltiples archivos de audio en un solo SoundEffect
        /// Nota: MonoGame/XNA no soporta mezcla en tiempo real fácilmente
        /// Esta función mezcla los archivos antes de crear el SoundEffect
        /// </summary>
        private SoundEffect MixAudioFiles(params string[] audioPaths)
        {
            var audioData = new List<(float[] samples, int sampleRate, int channels)>();
            
            // Cargar todos los archivos
            foreach (var path in audioPaths)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        using (var vorbis = new VorbisReader(path))
                        {
                            var buffer = new float[vorbis.TotalSamples * vorbis.Channels];
                            vorbis.ReadSamples(buffer, 0, buffer.Length);
                            audioData.Add((buffer, vorbis.SampleRate, vorbis.Channels));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ Error cargando {Path.GetFileName(path)}: {ex.Message}");
                    }
                }
            }
            
            if (audioData.Count == 0)
            {
                throw new Exception("No se pudo cargar ningún archivo de audio");
            }
            
            // Si solo hay un archivo, retornarlo directamente
            if (audioData.Count == 1)
            {
                return ConvertToSoundEffect(audioData[0].samples, audioData[0].sampleRate, audioData[0].channels);
            }
            
            // Mezclar múltiples archivos
            var (samples1, rate1, channels1) = audioData[0];
            var (samples2, rate2, channels2) = audioData[1];
            
            // Asegurar que tienen el mismo sample rate y canales
            if (rate1 != rate2 || channels1 != channels2)
            {
                Console.WriteLine("⚠️ Los archivos tienen diferentes formatos, usando solo el primero");
                return ConvertToSoundEffect(samples1, rate1, channels1);
            }
            
            // Mezclar samples
            int minLength = Math.Min(samples1.Length, samples2.Length);
            var mixed = new float[minLength];
            
            for (int i = 0; i < minLength; i++)
            {
                mixed[i] = (samples1[i] + samples2[i]) / 2f; // Promedio simple
            }
            
            return ConvertToSoundEffect(mixed, rate1, channels1);
        }
        
        /// <summary>
        /// Convierte samples float a SoundEffect
        /// </summary>
        private SoundEffect ConvertToSoundEffect(float[] samples, int sampleRate, int channels)
        {
            // Convertir float a short (16-bit PCM)
            var shortBuffer = new short[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                shortBuffer[i] = (short)(samples[i] * short.MaxValue);
            }
            
            // Convertir short a byte
            var byteBuffer = new byte[shortBuffer.Length * 2];
            Buffer.BlockCopy(shortBuffer, 0, byteBuffer, 0, byteBuffer.Length);
            
            return new SoundEffect(byteBuffer, sampleRate, (AudioChannels)channels);
        }
        
        #endregion
        
        #region Conversión a FunkyMakerPlayer
        
        /// <summary>
        /// Convierte el chart de Psych Engine al formato de FunkyMakerPlayer
        /// </summary>
        public FunkyMakerLevel ConvertToFunkyMakerLevel()
        {
            if (CurrentChart == null)
            {
                throw new InvalidOperationException("No hay chart cargado");
            }
            
            var level = new FunkyMakerLevel
            {
                BPM = CurrentChart.Bpm,
                Speed = CurrentChart.Speed,
                Notes = new List<FunkyMakerNote>()
            };
            
            // Convertir todas las notas
            var allNotes = GetAllNotes();
            
            foreach (var psychNote in allNotes)
            {
                var fmNote = new FunkyMakerNote
                {
                    Time = psychNote.Time / 1000f, // Convertir ms a segundos
                    Direction = psychNote.Direction,
                    Length = psychNote.Length / 1000f,
                    IsPlayerNote = psychNote.MustHitNote,
                    Type = psychNote.Type
                };
                
                level.Notes.Add(fmNote);
            }
            
            Console.WriteLine($"✅ Convertido: {level.Notes.Count} notas");
            return level;
        }
        
        /// <summary>
        /// Obtiene información del stage para FunkyMakerPlayer
        /// </summary>
        public FunkyMakerStageInfo GetStageInfo()
        {
            if (CurrentStage == null)
            {
                throw new InvalidOperationException("No hay stage cargado");
            }
            
            return new FunkyMakerStageInfo
            {
                DefaultZoom = CurrentStage.DefaultZoom,
                IsPixelStage = CurrentStage.IsPixelStage,
                BfPosition = new Vector2(CurrentStage.BfX, CurrentStage.BfY),
                OpponentPosition = new Vector2(CurrentStage.OppX, CurrentStage.OppY),
                GfPosition = new Vector2(CurrentStage.GfX, CurrentStage.GfY),
                LuaSprites = LuaSprites
            };
        }
        
        #endregion
        
        /// <summary>
        /// Limpia recursos
        /// </summary>
        public void Dispose()
        {
            AudioInstance?.Stop();
            AudioInstance?.Dispose();
            InstAudio?.Dispose();
            VoicesAudio?.Dispose();
            
            foreach (var texture in LoadedSprites.Values)
            {
                texture?.Dispose();
            }
            
            LoadedSprites.Clear();
            Characters.Clear();
        }
        
        /// <summary>
        /// Renderiza el gameplay de Psych Engine usando NoteRenderer de FNMM
        /// </summary>
        public void Draw(SpriteBatch spriteBatch, SpriteFont font, Texture2D pixel, int screenWidth, int screenHeight, float songTime, bool started, bool countdownDone)
        {
            if (CurrentChart == null) return;
            
            if (!started || !countdownDone)
            {
                // No mostrar gameplay durante countdown
                return;
            }
            
            // Renderizar stage (fondo)
            DrawStage(spriteBatch, screenWidth, screenHeight);
            
            // Renderizar personajes
            DrawCharacters(spriteBatch, songTime);
            
            // Renderizar notas usando NoteRenderer
            if (playerNoteRenderer != null)
            {
                playerNoteRenderer.Draw(spriteBatch, songTime, font);
            }
            
            if (opponentNoteRenderer != null)
            {
                opponentNoteRenderer.Draw(spriteBatch, songTime, font);
            }
            
            // Dibujar health bar
            DrawHealthBar(spriteBatch, pixel, font, screenWidth, screenHeight);
            
            // Dibujar score y combo (usar stats de playerNoteRenderer si existe)
            if (playerNoteRenderer != null)
            {
                DrawScoreFromRenderer(spriteBatch, font, screenWidth, screenHeight, playerNoteRenderer);
            }
            else
            {
                DrawScore(spriteBatch, font, screenWidth, screenHeight);
            }
        }
        
        /// <summary>
        /// Dibuja el stage (fondo y sprites Lua)
        /// </summary>
        private void DrawStage(SpriteBatch spriteBatch, int screenWidth, int screenHeight)
        {
            if (CurrentStage == null) return;
            
            // TODO: Renderizar sprites Lua del stage
            // Por ahora solo un fondo simple
            // Los sprites están en LuaSprites list
        }
        
        /// <summary>
        /// Dibuja los personajes con sus animaciones
        /// </summary>
        private void DrawCharacters(SpriteBatch spriteBatch, float songTime)
        {
            if (Characters.Count == 0) return;
            
            // TODO: Renderizar personajes con animaciones
            // Los personajes están en Characters dictionary
            // Necesitamos SpriteAnimator para las animaciones
        }
        
        /// <summary>
        /// Dibuja score usando stats del NoteRenderer
        /// </summary>
        private void DrawScoreFromRenderer(SpriteBatch spriteBatch, SpriteFont font, int sw, int sh, NoteRenderer renderer)
        {
            string scoreText = $"Score: {renderer.Score}";
            string comboText = renderer.Combo > 0 ? $"Combo: {renderer.Combo}" : "";
            string missText = $"Misses: {renderer.Misses}";
            
            // Score (arriba izquierda)
            spriteBatch.DrawString(font, scoreText, new Vector2(20, 20), Color.White, 0f, Vector2.Zero, 0.8f, SpriteEffects.None, 0f);
            
            // Misses (arriba izquierda, debajo del score)
            spriteBatch.DrawString(font, missText, new Vector2(20, 50), Color.Red * 0.8f, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
            
            // Combo (centro arriba)
            if (renderer.Combo > 0)
            {
                var comboSize = font.MeasureString(comboText);
                // Usar la escala animada con bounce (FNF style)
                spriteBatch.DrawString(font, comboText,
                    new Vector2(sw / 2f - comboSize.X * comboScale / 2f, 20),
                    Color.Yellow, 0f, Vector2.Zero, comboScale, SpriteEffects.None, 0f);
            }
            
            // Song info (arriba derecha)
            string songInfo = $"{CurrentSongName}\nBPM: {CurrentChart?.Bpm ?? 0}";
            var infoSize = font.MeasureString(songInfo) * 0.6f;
            spriteBatch.DrawString(font, songInfo,
                new Vector2(sw - infoSize.X - 20, 20),
                Color.White * 0.7f, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 0f);
        }
        
        /// <summary>
        /// Dibuja la barra de salud
        /// </summary>
        private void DrawHealthBar(SpriteBatch spriteBatch, Texture2D pixel, SpriteFont font, int sw, int sh)
        {
            // TODO: Obtener health de Game1 o implementar sistema de health
            // Por ahora usar un valor fijo
            float health = 0.5f;
            
            int barWidth = 400;
            int barHeight = 20;
            int barX = sw / 2 - barWidth / 2;
            int barY = sh - 50;
            
            // Fondo
            spriteBatch.Draw(pixel, new Rectangle(barX, barY, barWidth, barHeight), Color.Black * 0.5f);
            
            // Barra de salud
            int healthWidth = (int)(barWidth * health);
            Color healthColor = health > 0.5f ? Color.Lerp(Color.Yellow, Color.Green, (health - 0.5f) * 2f) :
                                                 Color.Lerp(Color.Red, Color.Yellow, health * 2f);
            spriteBatch.Draw(pixel, new Rectangle(barX, barY, healthWidth, barHeight), healthColor);
            
            // Borde
            DrawRectBorder(spriteBatch, pixel, new Rectangle(barX, barY, barWidth, barHeight), Color.White, 2);
            
            // Texto
            string healthText = $"Health: {(int)(health * 100)}%";
            var textSize = font.MeasureString(healthText) * 0.7f;
            spriteBatch.DrawString(font, healthText,
                new Vector2(sw / 2f - textSize.X / 2f, barY - textSize.Y - 5),
                Color.White, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
        }
        
        /// <summary>
        /// Dibuja el score y combo (fallback si no hay renderer)
        /// </summary>
        private void DrawScore(SpriteBatch spriteBatch, SpriteFont font, int sw, int sh)
        {
            string scoreText = "Score: 0";
            string missText = "Misses: 0";
            
            // Score (arriba izquierda)
            spriteBatch.DrawString(font, scoreText, new Vector2(20, 20), Color.White, 0f, Vector2.Zero, 0.8f, SpriteEffects.None, 0f);
            
            // Misses (arriba izquierda, debajo del score)
            spriteBatch.DrawString(font, missText, new Vector2(20, 50), Color.Red * 0.8f, 0f, Vector2.Zero, 0.7f, SpriteEffects.None, 0f);
            
            // Song info (arriba derecha)
            string songInfo = $"{CurrentSongName}\nBPM: {CurrentChart?.Bpm ?? 0}";
            var infoSize = font.MeasureString(songInfo) * 0.6f;
            spriteBatch.DrawString(font, songInfo,
                new Vector2(sw - infoSize.X - 20, 20),
                Color.White * 0.7f, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 0f);
        }
        
        /// <summary>
        /// Dibuja el borde de un rectángulo
        /// </summary>
        private void DrawRectBorder(SpriteBatch spriteBatch, Texture2D pixel, Rectangle rect, Color color, int thickness)
        {
            // Top
            spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
            // Bottom
            spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Y + rect.Height - thickness, rect.Width, thickness), color);
            // Left
            spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
            // Right
            spriteBatch.Draw(pixel, new Rectangle(rect.X + rect.Width - thickness, rect.Y, thickness, rect.Height), color);
        }
    }
    
    /// <summary>
    /// Representa un sprite cargado desde un script Lua de stage
    /// </summary>
    public class LuaSprite
    {
        public string Tag { get; set; } = "";
        public string Image { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public bool IsAnimated { get; set; }
        public bool IsBehind { get; set; }
        public float ScrollFactorX { get; set; } = 1f;
        public float ScrollFactorY { get; set; } = 1f;
        public float ScaleX { get; set; } = 1f;
        public float ScaleY { get; set; } = 1f;
        public float Alpha { get; set; } = 1f;
    }
    
    /// <summary>
    /// Nivel convertido al formato de FunkyMakerPlayer
    /// </summary>
    public class FunkyMakerLevel
    {
        public float BPM { get; set; }
        public float Speed { get; set; }
        public List<FunkyMakerNote> Notes { get; set; } = new List<FunkyMakerNote>();
    }
    
    /// <summary>
    /// Nota convertida al formato de FunkyMakerPlayer
    /// </summary>
    public class FunkyMakerNote
    {
        public float Time { get; set; }
        public int Direction { get; set; }
        public float Length { get; set; }
        public bool IsPlayerNote { get; set; }
        public string Type { get; set; } = "";
    }
    
    /// <summary>
    /// Información del stage para FunkyMakerPlayer
    /// </summary>
    public class FunkyMakerStageInfo
    {
        public float DefaultZoom { get; set; }
        public bool IsPixelStage { get; set; }
        public Vector2 BfPosition { get; set; }
        public Vector2 OpponentPosition { get; set; }
        public Vector2 GfPosition { get; set; }
        public List<LuaSprite> LuaSprites { get; set; } = new List<LuaSprite>();
    }
}
