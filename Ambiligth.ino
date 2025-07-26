#include <Adafruit_NeoPixel.h>

// ————— Configuración NeoPixel —————
#define MAX_PIXELS    64        // Máximo LEDs por tira
#define NUM_CELLS     8         // Celdas de color a repartir
#define NEO_RESET_US  300UL     // Tiempo de reset mínimo entre show()
#define NEO_PIN       3         // Pin de datos de la única tira
#define PREFIX_NEO    "NEO:"    // Prefijo de comando para captura de datos LED
#define PREFIX_RST    "RST:"    // Prefijo de comando para reseteo de placa


Adafruit_NeoPixel strip(MAX_PIXELS, NEO_PIN, NEO_GRB + NEO_KHZ800);

void (*resetFunc)(void) = 0;

void setup() {
  Serial.begin(250000);
  strip.begin();
  strip.show();  // Apagar al arrancar
  Serial.println(F("STA:STARTED|NEOPIXEL|0001"));
}

void loop() {
  leerSerial();
}

void leerSerial() {
  static char buf[256];
  static uint16_t idx = 0;

  while (Serial.available()) {
    char c = Serial.read();
    if (c == '\n' || c == '\r') {
      if (idx == 0) continue;       // Ignorar líneas vacías
      buf[idx] = '\0';
      if (!strncmp(buf, PREFIX_NEO, sizeof(PREFIX_NEO)-1)) {
        procesarNEO(buf + (sizeof(PREFIX_NEO)-1));
      }
      else if (!strncmp(buf, PREFIX_RST, sizeof(PREFIX_RST)-1))
        resetFunc(); 
      idx = 0;
    } else if (idx < sizeof(buf) - 1) {
      buf[idx++] = c;
    }
  }
}

void procesarNEO(char *linea) {
  // 1) Número de LEDs a actualizar
  char *tok = strtok(linea, "|");
  if (!tok) return;
  uint16_t numLeds = atoi(tok);
  if (numLeds < 1) {
    Serial.println(F("STA:ERROR|MIN NEO"));
    return;
  }
  if (numLeds > MAX_PIXELS) numLeds = MAX_PIXELS;

  // 2) Control de “busy” por reset timing
  static uint32_t lastShow = 0;
  uint32_t now = micros();
  if (now - lastShow < NEO_RESET_US) {
    Serial.println(F("STA:BUSY"));
    return;
  }

  // 3) Lectura de colores hex (NUM_CELLS celdas)
  uint32_t cols[NUM_CELLS];
  for (uint8_t c = 0; c < NUM_CELLS; c++) {
    tok = strtok(NULL, "|");
    cols[c] = (tok && *tok) ? strtoul(tok, NULL, 16) : 0;
  }

  // 4) Asignar colores repartidos entre numLeds
  for (uint16_t px = 0; px < numLeds; px++) {
    uint8_t cellIdx = (px * NUM_CELLS) / numLeds;
    uint32_t col = cols[cellIdx];
    strip.setPixelColor(px,
      strip.Color(
        (col >> 16) & 0xFF,
        (col >>  8) & 0xFF,
         col        & 0xFF
      )
    );
  }

  // 5) Mostrar y confirmar
  if (Serial.available() == 0) {
    strip.show();
    lastShow = micros();
    Serial.println(F("STA:OK"));
  }
}
