-- Thermal printers connected by network, USB or serial (COM) port, and the printer model, which sets the paper width
-- and characters per line (Epson TM-T88 at 180 dpi prints 42 characters on 80 mm paper, most 203 dpi printers 48).
--   printer_kind   network (address = IP[:port]) | usb (address = printer name in Windows) | serial (address = COM port)
--   printer_model  epson-tm-t88-80 | epson-tm-t88-58 | generic-80 | generic-58
--   printer_baud / printer_flow   serial port settings (speed, flow control: hardware | xonxoff | none)
ALTER TABLE fiscal.terminal DROP CONSTRAINT IF EXISTS terminal_printer_kind_check;
UPDATE fiscal.terminal SET printer_kind = 'usb' WHERE printer_kind = 'windows';
ALTER TABLE fiscal.terminal
    ADD CONSTRAINT terminal_printer_kind_check CHECK (printer_kind IN ('none', 'network', 'usb', 'serial')),
    ADD COLUMN printer_model text NOT NULL DEFAULT 'epson-tm-t88-80'
        CHECK (printer_model IN ('epson-tm-t88-80', 'epson-tm-t88-58', 'generic-80', 'generic-58')),
    ADD COLUMN printer_baud integer NOT NULL DEFAULT 38400 CHECK (printer_baud IN (9600, 19200, 38400, 57600, 115200)),
    ADD COLUMN printer_flow text NOT NULL DEFAULT 'hardware' CHECK (printer_flow IN ('hardware', 'xonxoff', 'none'));
UPDATE fiscal.terminal SET printer_model = 'generic-58' WHERE printer_paper_mm = 58;
