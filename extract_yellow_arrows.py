#!/usr/bin/env python3
"""
Extracts yellow arrow (NOTE_undyne, type:1) timings from hard.json
and creates a marker file for FMM
"""
import json

def extract_yellow_arrows(fnf_path, output_path):
    # Read FNF chart
    with open(fnf_path, 'r', encoding='utf-8') as f:
        fnf_data = json.load(f)
    
    # Get BPM
    fnf_bpm = fnf_data.get('meta', {}).get('bpm', 120)
    fmm_bpm = 150.0
    
    # Get player strumline (index 1)
    strum_lines = fnf_data.get('strumLines', [])
    if len(strum_lines) < 2:
        print("ERROR: Need player strumLine")
        return
    
    player_notes = strum_lines[1].get('notes', [])
    
    # Extract type:1 notes (NOTE_undyne / yellow arrows)
    yellow_arrows = []
    for note in player_notes:
        if note.get('type') == 1:
            time_ms = note.get('time', 0)
            lane = note.get('id', 0)
            
            # Convert to FMM beats
            time_seconds = time_ms / 1000.0
            time_beats = time_seconds * fmm_bpm / 60.0
            
            yellow_arrows.append({
                "time_beats": round(time_beats, 6),
                "lane": lane
            })
    
    # Save to JSON
    output_data = {
        "description": "Yellow arrow (NOTE_undyne) timings for Cornered",
        "count": len(yellow_arrows),
        "fmm_bpm": fmm_bpm,
        "yellow_arrows": yellow_arrows
    }
    
    with open(output_path, 'w', encoding='utf-8') as f:
        json.dump(output_data, f, indent=2)
    
    print(f"✓ Extracted {len(yellow_arrows)} yellow arrows")
    print(f"  Output: {output_path}")

if __name__ == '__main__':
    extract_yellow_arrows(
        'FunkyMakerPlayer/events/hard.json',
        'FunkyMakerPlayer/events/yellow_arrows.json'
    )
