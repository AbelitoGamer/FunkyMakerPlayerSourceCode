# Plan de Limpieza de Multiplayer en Game1.cs

## Áreas Identificadas para Eliminar/Simplificar

### 1. Variables de Estado (líneas ~175-192)
```csharp
// ELIMINAR:
private Multiplayer.MultiplayerMenu? _multiplayerMenu;
private bool _inMultiplayer = false;
public bool _isMultiplayerMatch = false;
private string _multiplayerOpponentName = "";
private long _multiplayerStartTimestamp = 0;
private int _multiplayerPlayerRole = 1;
private string _multiplayerLevelHash = "";
private int _lastHitLane = 0;
private Multiplayer.UI.ConnectionIndicator? _connectionIndicator;
private Multiplayer.MultiplayerHUD? _multiplayerHUD;
private Multiplayer.OpponentStateInterpolator? _opponentInterpolator;
```

**MANTENER SOLO:**
```csharp
private Multiplayer.MultiplayerMenu? _multiplayerMenu;  // Para acceder al menú
private bool _inMultiplayer = false;  // Para saber si estamos en el menú de multiplayer
```

### 2. Event Handlers (líneas ~518-522)
```csharp
// ELIMINAR ESTOS EVENTOS:
Multiplayer.NetworkManager.Instance.OnMatchFound += HandleMultiplayerMatchFound;
Multiplayer.NetworkManager.Instance.OnCountdownStart += HandleMultiplayerCountdownStart;
Multiplayer.NetworkManager.Instance.OnGameStart += HandleMultiplayerGameStart;
Multiplayer.NetworkManager.Instance.OnOpponentHit += HandleOpponentHit;
Multiplayer.NetworkManager.Instance.OnOpponentState += HandleOpponentState;
```

### 3. Inicialización de Componentes Multiplayer (líneas ~846-852)
```csharp
// SIMPLIFICAR:
_multiplayerMenu = new Multiplayer.MultiplayerMenu();  // MANTENER

// ELIMINAR:
_connectionIndicator = new Multiplayer.UI.ConnectionIndicator();
_multiplayerHUD = new Multiplayer.MultiplayerHUD(_connectionIndicator);
_opponentInterpolator = new Multiplayer.OpponentStateInterpolator();
```

### 4. Lógica de Chart Assignment en SetupLevel (líneas ~1749-1856)
```csharp
// ELIMINAR toda la lógica condicional de _isMultiplayerMatch
// MANTENER solo la lógica de single-player (normal)

// Ejemplo de código a eliminar:
if (_isMultiplayerMatch)
{
    // ... todo este bloque
}
```

### 5. LoadLevel con parámetro isMultiplayerMatch (línea ~627)
```csharp
// CAMBIAR:
_loadingScreen = new LoadingScreen(levelName, _pixel!, _font,
                   FindAssetsRoot(), GraphicsDevice, _isMultiplayerMatch);

// A:
_loadingScreen = new LoadingScreen(levelName, _pixel!, _font,
                   FindAssetsRoot(), GraphicsDevice, false);
```

### 6. Métodos Handler de Multiplayer
```csharp
// ELIMINAR COMPLETAMENTE:
private void HandleMultiplayerMatchFound(...)
private void HandleMultiplayerCountdownStart(...)
private void HandleMultiplayerGameStart(...)
private void HandleOpponentHit(...)
private void HandleOpponentState(...)
```

### 7. Update Loop - Multiplayer State Updates
```csharp
// ELIMINAR:
- Actualización de _connectionIndicator
- Actualización de _multiplayerHUD
- Actualización de _opponentInterpolator
- Lógica de sincronización de tiempo multiplayer
```

### 8. Draw Loop - Multiplayer UI
```csharp
// ELIMINAR:
- Dibujo de _multiplayerHUD
- Dibujo de _connectionIndicator
- Dibujo de nombres de oponentes
```

### 9. Input Handling - Multiplayer Commands
```csharp
// ELIMINAR:
- SendStateUpdate a NetworkManager
- Transmisión de hits a NetworkManager
- Ready/Cancel en multiplayer lobby
```

## Estrategia de Implementación

1. **Fase 1: Comentar en lugar de eliminar**
   - Comentar todo el código relacionado con multiplayer
   - Agregar comentarios: `// MOVED TO GameMultiplayer.cs`

2. **Fase 2: Verificar compilación**
   - Asegurar que Game1.cs compile sin errores
   - Verificar que single-player funcione

3. **Fase 3: Eliminar código comentado**
   - Una vez confirmado que todo funciona
   - Hacer commit limpio

## Archivos Afectados

- `Game1.cs` - Principal
- Posiblemente `LoadingScreen.cs` - Remover parámetro isMultiplayerMatch

## Resultado Esperado

`Game1.cs` debe quedar como:
- ✅ Modo single-player funcional
- ✅ Acceso al menú de multiplayer (_multiplayerMenu)
- ❌ Sin lógica de partidas multijugador (eso está en GameMultiplayer.cs)
- ❌ Sin sincronización de red durante gameplay
- ❌ Sin UI de multiplayer durante gameplay
