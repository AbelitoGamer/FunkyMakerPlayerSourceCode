using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FunkyMakerPlayer;

/// <summary>
/// FNF-style song transition: AnkushCats cascade across the screen filling it up,
/// then fade out. Used when a song ends and when a new song starts.
/// </summary>
public class SongTransition
{
    public enum Phase { Filling, Full, Clearing, Done }
    public Phase CurrentPhase { get; private set; } = Phase.Filling;

    private readonly Texture2D? _catTex;
    private readonly Texture2D  _pixel;

    // Grid of cats
    private struct CatCell
    {
        public float Delay;     // seconds before this cat appears
        public float Alpha;     // 0..1
        public float Scale;     // pop-in scale
        public bool  Appeared;
    }

    private CatCell[,] _grid = new CatCell[0, 0];
    private int _cols, _rows;
    private int _catSize = 80;  // pixel size of each cat cell
    private float _timer = 0f;
    private float _fullTimer = 0f;
    private const float FULL_HOLD   = 0.5f;  // how long to stay fully covered
    private const float FILL_WINDOW = 0.6f;  // total time window to spread all random delays across
    private const float POP_SPEED   = 10f;   // scale animation speed
    private const float CLEAR_SPEED = 5f;    // fade-out speed

    private bool _initialized = false;
    private int _lastWidth = 0;
    private int _lastHeight = 0;

    public SongTransition(Texture2D? catTex, Texture2D pixel)
    {
        _catTex = catTex;
        _pixel  = pixel;
    }

    public void Start(int sw, int sh)
    {
        _cols = sw / _catSize + 2;
        _rows = sh / _catSize + 2;
        _grid = new CatCell[_cols, _rows];
        _timer = 0f;
        _fullTimer = 0f;
        CurrentPhase = Phase.Filling;
        _initialized = true;
        _lastWidth = sw;
        _lastHeight = sh;

        // Assign fully random delays — cats pop in at random positions all over
        // the screen, not in a wave. Seed with current time so each transition looks different.
        var rng = new Random();
        for (int x = 0; x < _cols; x++)
        {
            for (int y = 0; y < _rows; y++)
            {
                _grid[x, y] = new CatCell
                {
                    Delay    = (float)rng.NextDouble() * FILL_WINDOW,
                    Alpha    = 0f,
                    Scale    = 0f,
                    Appeared = false,
                };
            }
        }
    }

    public void Update(float dt)
    {
        if (!_initialized) return;
        _timer += dt;

        switch (CurrentPhase)
        {
            case Phase.Filling:
            {
                bool allIn = true;
                for (int x = 0; x < _cols; x++)
                {
                    for (int y = 0; y < _rows; y++)
                    {
                        ref var cell = ref _grid[x, y];
                        if (_timer >= cell.Delay)
                        {
                            cell.Appeared = true;
                            cell.Scale = Math.Min(1f, cell.Scale + dt * POP_SPEED);
                            cell.Alpha = Math.Min(1f, cell.Alpha + dt * POP_SPEED);
                        }
                        if (cell.Alpha < 1f) allIn = false;
                    }
                }
                if (allIn)
                {
                    CurrentPhase = Phase.Full;
                    _fullTimer = 0f;
                }
                break;
            }

            case Phase.Full:
            {
                _fullTimer += dt;
                if (_fullTimer >= FULL_HOLD)
                    CurrentPhase = Phase.Clearing;
                break;
            }

            case Phase.Clearing:
            {
                bool allOut = true;
                for (int x = 0; x < _cols; x++)
                {
                    for (int y = 0; y < _rows; y++)
                    {
                        ref var cell = ref _grid[x, y];
                        cell.Alpha = Math.Max(0f, cell.Alpha - dt * CLEAR_SPEED);
                        if (cell.Alpha > 0f) allOut = false;
                    }
                }
                if (allOut)
                    CurrentPhase = Phase.Done;
                break;
            }
        }
    }

    public void Draw(SpriteBatch sb, int sw, int sh)
    {
        if (!_initialized || CurrentPhase == Phase.Done) return;

        // Si las dimensiones cambiaron (ej: render target → ventana), re-escalar el grid
        if (sw != _lastWidth || sh != _lastHeight)
        {
            // Guardar el estado actual de la transición
            Phase oldPhase = CurrentPhase;
            float oldTimer = _timer;
            float oldFullTimer = _fullTimer;
            var oldGrid = _grid;
            int oldCols = _cols;
            int oldRows = _rows;
            
            // Calcular nuevas dimensiones
            _cols = sw / _catSize + 2;
            _rows = sh / _catSize + 2;
            _grid = new CatCell[_cols, _rows];
            _lastWidth = sw;
            _lastHeight = sh;
            
            // Si ya pasamos la fase de Filling, todos los stickers deben estar visibles
            bool allShouldAppear = (oldPhase == Phase.Full || oldPhase == Phase.Clearing);
            
            // Crear nuevo grid y mapear el estado antiguo
            var rng = new Random();
            for (int newX = 0; newX < _cols; newX++)
            {
                for (int newY = 0; newY < _rows; newY++)
                {
                    // Mapear posición nueva → posición antigua proporcional
                    float normX = (float)newX / _cols;
                    float normY = (float)newY / _rows;
                    int oldX = (int)(normX * oldCols);
                    int oldY = (int)(normY * oldRows);
                    
                    // Si hay celda antigua en esa posición, copiar su estado
                    if (oldX < oldCols && oldY < oldRows)
                    {
                        var oldCell = oldGrid[oldX, oldY];
                        _grid[newX, newY] = new CatCell
                        {
                            Delay = oldCell.Delay,
                            Alpha = oldCell.Alpha,
                            Scale = oldCell.Scale,
                            Appeared = oldCell.Appeared || allShouldAppear
                        };
                    }
                    else
                    {
                        // Nueva celda que no existía en el grid anterior
                        _grid[newX, newY] = new CatCell
                        {
                            Delay = allShouldAppear ? 0f : (float)rng.NextDouble() * FILL_WINDOW,
                            Alpha = allShouldAppear ? 1f : 0f,
                            Scale = allShouldAppear ? 1f : 0f,
                            Appeared = allShouldAppear
                        };
                    }
                }
            }
            
            // Restaurar el estado de la transición
            CurrentPhase = oldPhase;
            _timer = oldTimer;
            _fullTimer = oldFullTimer;
        }

        for (int x = 0; x < _cols; x++)
        {
            for (int y = 0; y < _rows; y++)
            {
                ref var cell = ref _grid[x, y];
                if (!cell.Appeared || cell.Alpha <= 0f) continue;

                float px = x * _catSize;
                float py = y * _catSize;
                float size = _catSize * cell.Scale;
                float offset = (_catSize - size) / 2f;

                if (_catTex != null)
                {
                    sb.Draw(_catTex,
                        new Rectangle((int)(px + offset), (int)(py + offset), (int)size, (int)size),
                        Color.White * cell.Alpha);
                }
                else
                {
                    // Fallback: colored squares
                    sb.Draw(_pixel,
                        new Rectangle((int)(px + offset), (int)(py + offset), (int)size, (int)size),
                        Color.Cyan * cell.Alpha);
                }
            }
        }
    }
}
