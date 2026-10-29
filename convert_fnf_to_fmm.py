#!/usr/bin/env python3
"""
Converts FNF hard.json chart to FMM data.json format
"""
import json
import sys

def convert_fnf_to_fmm(fnf_path, output_path):
    # Read FNF chart
    with open(fnf_path, 'r', encoding='utf-8') as f:
        fnf_data = json.load(f)
    
    # Get BPM from FNF
    fnf_bpm = fnf_data.get('meta', {}).get('bpm', 120)
    print(f"FNF BPM: {fnf_bpm}")
    
    # FMM uses 150 BPM for this song
    fmm_bpm = 150.0
    
    # Get strumLines
    strum_lines = fnf_data.get('strumLines', [])
    if len(strum_lines) < 2:
        print("ERROR: Need at least 2 strumLines (opponent + player)")
        return
    
    # Prepare FMM structure
    # FMM format: strumLines[strumline_index][lane_index][note_list]
    fmm_strumlines = []
    
    # Process each strumline (0=opponent, 1=player)
    for strumline_idx, strumline in enumerate(strum_lines[:2]):
        notes = strumline.get('notes', [])
        
        # Create 4 lanes (left, down, up, right)
        lanes = [[], [], [], []]
        
        for note in notes:
            time_ms = note.get('time', 0)
            lane_id = note.get('id', 0)
            note_type = note.get('type', 0)
            length_ms = note.get('length', 0)
            
            # Skip invalid lanes
            if lane_id < 0 or lane_id >= 4:
                continue
            
            # Convert time: milliseconds -> seconds -> beats (using FMM BPM)
            time_seconds = time_ms / 1000.0
            time_beats = time_seconds * fmm_bpm / 60.0
            
            # Convert duration
            duration_seconds = length_ms / 1000.0
            duration_beats = duration_seconds * fmm_bpm / 60.0
            
            # Determine skin based on type
            # type 0 = normal
            # type 1 = NOTE_undyne (yellow arrow - we'll mark as normal and handle in code)
            # type 2 = No Anim
            skin = 0.0  # All notes as normal for now
            
            # Create FMM note
            fmm_note = {
                "start": round(time_beats, 6),
                "duration": round(duration_beats, 6),
                "props": {
                    "skin": skin,
                    "onPressEvent": -1.0,
                    "onMissEvent": -1.0
                }
            }
            
            lanes[lane_id].append(fmm_note)
        
        # Sort each lane by start time
        for lane in lanes:
            lane.sort(key=lambda n: n['start'])
        
        fmm_strumlines.append(lanes)
        print(f"Strumline {strumline_idx}: {sum(len(lane) for lane in lanes)} notes")
    
    # Create FMM data structure
    fmm_data = {
        "bpm": fmm_bpm,
        "version": 4.0,
        "npb": 8.0,  # Notes per beat (standard)
        "speed": 1.0,
        "strumlines": fmm_strumlines
    }
    
    # Write to output
    with open(output_path, 'w', encoding='utf-8') as f:
        json.dump(fmm_data, f, indent=2)
    
    print(f"\n✓ Conversion complete!")
    print(f"  Input:  {fnf_path}")
    print(f"  Output: {output_path}")
    print(f"  FNF BPM: {fnf_bpm} -> FMM BPM: {fmm_bpm}")
    print(f"  Total notes: {sum(sum(len(lane) for lane in strumline) for strumline in fmm_strumlines)}")

if __name__ == '__main__':
    if len(sys.argv) < 2:
        fnf_path = 'FunkyMakerPlayer/events/hard.json'
        output_path = 'FunkyMakerPlayer/bin/Debug/net10.0/mods/levels/Cornered/01kd72pw682p6mrj4wg388zgz5-01kd72pw6b7cgb0nr3ven4rvaf/data_converted.json'
    else:
        fnf_path = sys.argv[1]
        output_path = sys.argv[2] if len(sys.argv) > 2 else 'data_converted.json'
    
    convert_fnf_to_fmm(fnf_path, output_path)
