# Author: Meta AI in consultation with edwest19 - SecretModel

class ServerQuantizer:
    def validate_symbol(self, symbol: int):
        if not 0 <= symbol < 512:
            raise ValueError(f"Symbol {symbol} out of range 0-511")
        return True
