﻿using Phantasma.Core.Log;
using System;
using System.Collections.Generic;

namespace Phantasma.CLI
{
    public class ConsoleOutput: Logger
    {
        private byte[] logo;
        private ConsoleColor defaultBG;
        private List<KeyValuePair<LogEntryKind, string>> _text = new List<KeyValuePair<LogEntryKind, string>>();
        private int lastIndex;
        private bool shouldRedraw;

        private bool ready = false;
        private bool initializing = true;
        private int animationCounter = 0;
        private DateTime lastRedraw;

        public ConsoleOutput()
        {
            Console.ResetColor();
            this.defaultBG = Console.BackgroundColor;

            this.logo = Logo.GetPixels();
           
            Update();
        }

        public void MakeReady()
        {
            ready = true;
        }

        public override void Write(LogEntryKind kind, string msg)
        {
            InternalWrite(kind, msg);
        }

        private void InternalWrite(LogEntryKind kind, string msg)
        {
            lock (_text)
            {
                _text.Add(new KeyValuePair<LogEntryKind, string>(kind, msg));
                shouldRedraw = true;
            }
        }

        private void FillLine(ConsoleColor fg, char symbol)
        {
            Console.ForegroundColor = fg;

            for (int i= Console.CursorLeft; i <Console.WindowWidth; i++)
            {
                Console.Write(symbol);
            }
        }

        private void Redraw()
        {
            //Console.Clear();
            Console.CursorVisible = false;
            Console.SetCursorPosition(0, 0);
            FillLine(ConsoleColor.DarkCyan, '.');

            int midX = Console.WindowWidth / 2;
            int lX = midX - (Logo.Width / 2);

            int lY = 1;
            for (int j = 0; j < Logo.Height; j++)
            {
                Console.SetCursorPosition(lX, j + lY);
                for (int i = 0; i < Logo.Width; i++)
                {
                    var pixel = logo[i + j * Logo.Width];
                    switch (pixel)
                    {
                        case 1: Console.BackgroundColor = ConsoleColor.DarkCyan; break;
                        case 2: Console.BackgroundColor = ConsoleColor.Cyan; break;
                        case 3: Console.BackgroundColor = ConsoleColor.White; break;
                        default: Console.BackgroundColor = defaultBG; break;
                    }
                    Console.Write(" ");
                }
            }
            Console.BackgroundColor = defaultBG;

            int curY = Logo.Height + lY;

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.SetCursorPosition(0, curY);

            if (initializing)
            {
                Console.Write("Booting Phantasma node");
                int dots = animationCounter % 4;
                for (int i=0; i<dots; i++)
                {
                    Console.Write(".");
                }                
            }
            else
            {
                Console.Write(">Ready");
            }
            FillLine(ConsoleColor.White, ' ');

            curY++;
            Console.SetCursorPosition(0, curY);
            FillLine(ConsoleColor.DarkCyan, '.');

            curY++;
            int maxLines = (Console.WindowHeight-1) - (curY + 3); // this might be wrong...

            for (int i = 0; i < _text.Count; i++)
            {
                Console.SetCursorPosition(0, curY + i);
                Console.Write(_text[i]);
                FillLine(ConsoleColor.DarkCyan, ' ');

                if (i > maxLines)
                {
                    if (_text.Count > maxLines)
                    {
                        _text.RemoveAt(0);
                        shouldRedraw = true;
                    }
                    else
                    if (ready)
                    {
                        initializing = false;
                        ready = false;
                    }
                    break;
                }
            }
            FillLine(ConsoleColor.DarkCyan, '.');
        }

        public void Update()
        {
            if (initializing)
            {
                var diff = DateTime.UtcNow - lastRedraw;
                if (diff.TotalSeconds >= 1)
                {
                    lastRedraw = DateTime.UtcNow;
                    animationCounter++;
                    shouldRedraw = true;
                }
            }

            if (shouldRedraw) {
                shouldRedraw = false;
                lock (_text)
                {
                    Redraw();
                }
            }
        }
    }
}